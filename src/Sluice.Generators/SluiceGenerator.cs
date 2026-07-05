using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Sluice.Generators;

[Generator]
public sealed class SluiceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interfaces = context
            .SyntaxProvider.ForAttributeWithMetadataName(
                "Sluice.SluiceAttribute",
                predicate: static (node, _) => node is InterfaceDeclarationSyntax,
                transform: static (ctx, _) => Transform(ctx)
            )
            .Where(static x => x is not null)
            .Select(static (x, _) => x!);

        context.RegisterSourceOutput(interfaces, Emit);
    }

    private sealed record InterfaceData(
        string InterfaceName,
        string GeneratedClassName,
        List<MethodData> ReadMethods,
        List<MethodData> WriteMethods
    );

    private sealed record MethodData(
        string MethodName,
        string KeyType,
        string? ValueType,
        List<ResourceAttribute> Resources,
        List<string> ExtraParameterTypes,
        List<string> ExtraParameterNames
    );

    private sealed record ResourceAttribute(bool IsCollection, string Name, string FieldName);

    private static InterfaceData? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        var syntax = (InterfaceDeclarationSyntax)ctx.TargetNode;
        var model = ctx.SemanticModel;
        var typeSymbol = model.GetDeclaredSymbol(syntax) as INamedTypeSymbol;
        if (typeSymbol is null)
            return null;

        var interfaceName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var simpleName = typeSymbol.Name;
        var className = simpleName.StartsWith("I") ? simpleName.Substring(1) : simpleName;

        string? customName = null;
        foreach (var attr in ctx.Attributes)
        {
            foreach (var kv in attr.NamedArguments)
            {
                if (kv.Key is "Name" && kv.Value.Value is string s)
                    customName = s;
            }
        }

        var finalClassName = customName ?? className;
        var readMethods = new List<MethodData>();
        var writeMethods = new List<MethodData>();

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            var methodAttrs = method.GetAttributes();
            if (methodAttrs.Length == 0)
                continue;

            bool isRead = false;
            bool isWrite = false;
            var resources = new List<ResourceAttribute>();

            foreach (var ma in methodAttrs)
            {
                var fullName = ma.AttributeClass?.ToDisplayString();
                if (fullName is null)
                    continue;

                if (fullName is "Sluice.ReadEntityAttribute")
                {
                    isRead = true;
                    var name = (string)ma.ConstructorArguments[0].Value!;
                    resources.Add(new ResourceAttribute(false, name, ToIdentifier(name)));
                }
                else if (fullName is "Sluice.ReadCollectionAttribute")
                {
                    isRead = true;
                    var collection = (string)ma.ConstructorArguments[0].Value!;
                    var byKey = (string)ma.ConstructorArguments[1].Value!;
                    var resourceName = $"{collection}.{byKey}";
                    resources.Add(
                        new ResourceAttribute(true, resourceName, ToIdentifier(resourceName))
                    );
                }
                else if (fullName is "Sluice.WriteEntityAttribute")
                {
                    isWrite = true;
                    var name = (string)ma.ConstructorArguments[0].Value!;
                    resources.Add(new ResourceAttribute(false, name, ToIdentifier(name)));
                }
                else if (fullName is "Sluice.WriteCollectionAttribute")
                {
                    isWrite = true;
                    var collection = (string)ma.ConstructorArguments[0].Value!;
                    var byKey = (string)ma.ConstructorArguments[1].Value!;
                    var resourceName = $"{collection}.{byKey}";
                    resources.Add(
                        new ResourceAttribute(true, resourceName, ToIdentifier(resourceName))
                    );
                }
            }

            if (!isRead && !isWrite)
                continue;

            var parameters = method.Parameters;
            if (parameters.Length < 2)
                continue;

            var keyParam = parameters[0];
            var keyType = keyParam.Type.ToDisplayString();

            var lastParam = parameters[parameters.Length - 1];
            bool hasCancellationToken =
                lastParam.Type.ToDisplayString()
                is "System.Threading.CancellationToken"
                    or "CancellationToken";

            if (isRead)
            {
                if (!hasCancellationToken || parameters.Length != 2)
                    continue;

                var returnType = method.ReturnType as INamedTypeSymbol;
                if (
                    returnType is null
                    || returnType.Name != "Task"
                    || returnType.TypeArguments.Length != 1
                )
                {
                    continue;
                }

                var valueType = returnType.TypeArguments[0].ToDisplayString();
                readMethods.Add(new MethodData(method.Name, keyType, valueType, resources, [], []));
            }
            else if (isWrite)
            {
                if (!hasCancellationToken)
                    continue;

                var extraParams = new List<string>();
                var extraNames = new List<string>();
                for (int i = 1; i < parameters.Length - 1; i++)
                {
                    extraParams.Add(parameters[i].Type.ToDisplayString());
                    extraNames.Add(parameters[i].Name);
                }

                writeMethods.Add(
                    new MethodData(method.Name, keyType, null, resources, extraParams, extraNames)
                );
            }
        }

        if (readMethods.Count == 0 && writeMethods.Count == 0)
            return null;

        return new InterfaceData(interfaceName, finalClassName, readMethods, writeMethods);
    }

    private static void Emit(SourceProductionContext context, InterfaceData data)
    {
        var interfaceName = data.InterfaceName;
        var className = data.GeneratedClassName;
        var resourcesName = $"{className}Resources";
        var sluiceName = $"{className}Sluice";

        var resourceSb = new StringBuilder();
        resourceSb.AppendLine("#pragma warning disable CS1591");
        resourceSb.AppendLine();
        resourceSb.AppendLine("using Sluice;");
        resourceSb.AppendLine();
        resourceSb.AppendLine($"public static class {resourcesName}");
        resourceSb.AppendLine("{");

        var allResources = new List<(ResourceAttribute Res, bool IsRead)>();
        foreach (var rm in data.ReadMethods)
        foreach (var r in rm.Resources)
            if (!allResources.Any(x => x.Res.Name == r.Name))
                allResources.Add((r, true));

        foreach (var wm in data.WriteMethods)
        foreach (var r in wm.Resources)
            if (!allResources.Any(x => x.Res.Name == r.Name))
                allResources.Add((r, false));

        var commonKeyType =
            data.ReadMethods.Select(m => m.KeyType)
                .Concat(data.WriteMethods.Select(m => m.KeyType))
                .FirstOrDefault()
            ?? "TKey";

        foreach (var (res, _) in allResources)
        {
            var kind = res.IsCollection ? "CollectionResource" : "EntityResource";
            var escapedName = res.Name.Replace("\"", "\\\"");
            resourceSb.AppendLine(
                $"    public static readonly {kind}<{commonKeyType}> {res.FieldName} = new(\"{escapedName}\");"
            );
        }

        resourceSb.AppendLine("}");
        resourceSb.AppendLine();

        var sluiceSb = new StringBuilder();
        sluiceSb.AppendLine("#pragma warning disable CS1591");
        sluiceSb.AppendLine();
        sluiceSb.AppendLine("using Sluice;");
        sluiceSb.AppendLine();
        sluiceSb.AppendLine(
            $"public sealed class {sluiceName}(ISluice sluice, {interfaceName} store)"
        );
        sluiceSb.AppendLine("{");

        foreach (var rm in data.ReadMethods)
        {
            var fieldName = StripReadPrefix(rm.MethodName);
            var keyType = rm.KeyType;
            var valueType = rm.ValueType!;
            var resourceField = rm.Resources[0].FieldName;
            sluiceSb.AppendLine(
                $"    public readonly TrackedRead<{keyType}, {valueType}> {fieldName} = new("
            );
            sluiceSb.AppendLine(
                $"        {resourcesName}.{resourceField}.For, store.{rm.MethodName});"
            );
            sluiceSb.AppendLine();
        }

        foreach (var wm in data.WriteMethods)
        {
            var fieldName = $"_{ToCamelCase(wm.MethodName)}";
            var keyType = wm.KeyType;
            var resourceFields = wm.Resources.Select(r => $"{resourcesName}.{r.FieldName}.For");
            var resourceList = string.Join(", ", resourceFields);

            sluiceSb.AppendLine($"    private readonly TrackedWrite<{keyType}> {fieldName} = new(");
            sluiceSb.AppendLine($"        sluice,");
            sluiceSb.AppendLine($"        {resourceList});");
            sluiceSb.AppendLine();

            var extraParamList = new StringBuilder();
            var extraCallList = new StringBuilder();
            for (int i = 0; i < wm.ExtraParameterNames.Count; i++)
            {
                if (i > 0)
                {
                    extraParamList.Append(", ");
                    extraCallList.Append(", ");
                }
                extraParamList.Append($"{wm.ExtraParameterTypes[i]} {wm.ExtraParameterNames[i]}");
                extraCallList.Append(wm.ExtraParameterNames[i]);
            }

            if (extraParamList.Length > 0)
                extraParamList.Append(", ");
            if (extraCallList.Length > 0)
                extraCallList.Append(", ");

            sluiceSb.AppendLine(
                $"    public Task {wm.MethodName}({keyType} id, {extraParamList}CancellationToken ct) =>"
            );
            sluiceSb.AppendLine(
                $"        {fieldName}.Write(id, ct => store.{wm.MethodName}(id, {extraCallList}ct), ct);"
            );
            sluiceSb.AppendLine();
        }

        sluiceSb.AppendLine("}");

        context.AddSource(
            $"{resourcesName}.g.cs",
            SourceText.From(resourceSb.ToString(), Encoding.UTF8)
        );
        context.AddSource(
            $"{sluiceName}.g.cs",
            SourceText.From(sluiceSb.ToString(), Encoding.UTF8)
        );
    }

    private static string StripReadPrefix(string name)
    {
        var prefixes = new[] { "Get", "Read", "Fetch", "Load" };
        foreach (var p in prefixes)
        {
            if (name.StartsWith(p) && name.Length > p.Length)
                return name.Substring(p.Length);
        }
        return name;
    }

    private static string ToIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "Resource";

        var sb = new StringBuilder();
        bool capitalizeNext = true;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(capitalizeNext ? char.ToUpperInvariant(ch) : ch);
                capitalizeNext = false;
            }
            else
            {
                capitalizeNext = true;
            }
        }
        return sb.ToString();
    }

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "_";
        var sb = new StringBuilder();
        sb.Append(char.ToLowerInvariant(name[0]));
        for (int i = 1; i < name.Length; i++)
            sb.Append(name[i]);
        return sb.ToString();
    }
}
