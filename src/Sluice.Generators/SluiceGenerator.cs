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

    private sealed record DiagnosticInfo(
        string Id,
        string Message
    );

    private sealed record InterfaceData(
        string InterfaceName,
        string GeneratedClassName,
        string Namespace,
        List<MethodData> ReadMethods,
        List<MethodData> WriteMethods,
        List<DiagnosticInfo> Diagnostics
    );

    private sealed record MethodData(
        string MethodName,
        string KeyType,
        string? ValueType,
        List<ResourceAttribute> Resources,
        List<string> ExtraParameterTypes,
        List<string> ExtraParameterNames
    );

    private sealed record ResourceAttribute(
        bool IsCollection,
        string Name,
        string FieldName,
        string KeyType,
        string? ResultKey = null
    );

    private static InterfaceData? Transform(GeneratorAttributeSyntaxContext ctx)
    {
        var syntax = (InterfaceDeclarationSyntax)ctx.TargetNode;
        var model = ctx.SemanticModel;
        var typeSymbol = model.GetDeclaredSymbol(syntax) as INamedTypeSymbol;
        if (typeSymbol is null)
            return null;

        var compilation = model.Compilation;
        var resourceKeySymbol = compilation.GetTypeByMetadataName("Sluice.IResourceKey");

        var interfaceName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var simpleName = typeSymbol.Name;
        var className = simpleName.StartsWith("I") ? simpleName.Substring(1) : simpleName;

        var ns = typeSymbol.ContainingNamespace;
        var namespaceStr = ns.IsGlobalNamespace ? "" : ns.ToDisplayString();

        string? customName = null;
        foreach (var attr in ctx.Attributes)
        {
            if (customName is null && attr.ConstructorArguments.Length > 0)
            {
                var ctorArg = attr.ConstructorArguments[0];
                if (ctorArg.Kind == TypedConstantKind.Primitive && ctorArg.Value is string s)
                    customName = s;
            }
        }

        var diagnostics = new List<DiagnosticInfo>();
        string? suppressedName = null;
        if (customName is not null && customName.EndsWith("Sluice", StringComparison.Ordinal))
        {
            diagnostics.Add(new DiagnosticInfo(
                "SLUICE002",
                $"Custom name '{customName}' already ends with 'Sluice' - use the base name instead."
            ));
            suppressedName = customName;
        }

        var finalClassName = suppressedName is not null ? className : (customName ?? className);

        var readMethods = new List<MethodData>();
        var writeMethods = new List<MethodData>();

        if (suppressedName is not null)
        {
            return new InterfaceData(
                interfaceName,
                finalClassName,
                namespaceStr,
                readMethods,
                writeMethods,
                diagnostics
            );
        }

        foreach (var member in typeSymbol.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;

            var methodAttrs = method.GetAttributes();
            if (methodAttrs.Length == 0)
                continue;

            bool isRead = false;
            bool isWrite = false;
            var tempResources = new List<(bool IsCollection, string Name, string? ResultKey)>();

            foreach (var ma in methodAttrs)
            {
                var fullName = ma.AttributeClass?.ToDisplayString();
                if (fullName is null)
                    continue;

                if (fullName is "Sluice.ReadEntityAttribute")
                {
                    isRead = true;
                    var name = (string)ma.ConstructorArguments[0].Value!;
                    tempResources.Add((false, name, null));
                }
                else if (fullName is "Sluice.ReadCollectionAttribute")
                {
                    isRead = true;
                    var collection = (string)ma.ConstructorArguments[0].Value!;
                    var byKey = (string)ma.ConstructorArguments[1].Value!;
                    var resourceName = $"{collection}.{byKey}";
                    tempResources.Add((true, resourceName, null));
                }
                else if (fullName is "Sluice.WriteEntityAttribute")
                {
                    isWrite = true;
                    var name = (string)ma.ConstructorArguments[0].Value!;
                    string? resultKey = null;
                    foreach (var kv in ma.NamedArguments)
                        if (kv.Key is "ResultKey" && kv.Value.Value is string s)
                            resultKey = s;
                    tempResources.Add((false, name, resultKey));
                }
                else if (fullName is "Sluice.WriteCollectionAttribute")
                {
                    isWrite = true;
                    var collection = (string)ma.ConstructorArguments[0].Value!;
                    var byKey = (string)ma.ConstructorArguments[1].Value!;
                    var resourceName = $"{collection}.{byKey}";
                    string? resultKey = null;
                    foreach (var kv in ma.NamedArguments)
                        if (kv.Key is "ResultKey" && kv.Value.Value is string s)
                            resultKey = s;
                    tempResources.Add((true, resourceName, resultKey));
                }
            }

            if (!isRead && !isWrite)
                continue;

            var parameters = method.Parameters;

            if (parameters.Length == 0)
            {
                diagnostics.Add(new DiagnosticInfo(
                    "SLUICE003",
                    $"Method '{method.Name}' has fewer than 2 parameters. Expected at least a key parameter and CancellationToken."
                ));
                continue;
            }

            var keyParam = parameters[0];
            var keyType = keyParam.Type.ToDisplayString();

            if (resourceKeySymbol is not null)
            {
                bool implementsIResourceKey = keyParam.Type.Equals(resourceKeySymbol, SymbolEqualityComparer.Default)
                    || keyParam.Type.AllInterfaces.Any(iface =>
                        iface is not null
                        && SymbolEqualityComparer.Default.Equals(iface, resourceKeySymbol)
                    );
                if (!implementsIResourceKey)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE004",
                        $"Method '{method.Name}': first parameter type '{keyType}' does not implement IResourceKey."
                    ));
                    continue;
                }
            }

            var lastParam = parameters[parameters.Length - 1];
            bool hasCancellationToken =
                lastParam.Type.ToDisplayString()
                is "System.Threading.CancellationToken"
                    or "CancellationToken";

            if (isRead)
            {
                if (parameters.Length != 2)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE003",
                        $"Method '{method.Name}': read method must have exactly 2 parameters (key + CancellationToken). Has {parameters.Length}."
                    ));
                    continue;
                }

                if (!hasCancellationToken)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE003",
                        $"Method '{method.Name}': read method is missing a final CancellationToken parameter."
                    ));
                    continue;
                }

                var returnType = method.ReturnType as INamedTypeSymbol;
                if (
                    returnType is null
                    || returnType.Name != "Task"
                    || returnType.TypeArguments.Length != 1
                )
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE003",
                        $"Method '{method.Name}': read method must return Task<T>. Has {method.ReturnType}."
                    ));
                    continue;
                }

                var valueType = returnType.TypeArguments[0].ToDisplayString();

                bool readHasColon = false;
                foreach (var (_, name, _) in tempResources)
                {
                    if (name.Contains(':'))
                    {
                        readHasColon = true;
                        diagnostics.Add(new DiagnosticInfo(
                            "SLUICE005",
                            $"Method '{method.Name}': resource name '{name}' contains ':' which is not allowed (address delimiter)."
                        ));
                    }
                }

                if (readHasColon)
                {
                    continue;
                }

                var resources = tempResources
                    .Select(t => new ResourceAttribute(
                        t.IsCollection,
                        t.Name,
                        ToIdentifier(t.Name),
                        keyType
                    ))
                    .ToList();
                readMethods.Add(new MethodData(method.Name, keyType, valueType, resources, [], []));
            }
            else if (isWrite)
            {
                if (!hasCancellationToken)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE003",
                        $"Method '{method.Name}': write method is missing a final CancellationToken parameter."
                    ));
                    continue;
                }

                string? valueType = null;
                INamedTypeSymbol? resultTypeSymbol = null;
                var returnType = method.ReturnType as INamedTypeSymbol;
                if (
                    returnType is not null
                    && returnType.Name == "Task"
                    && returnType.TypeArguments.Length == 1
                )
                {
                    valueType = returnType.TypeArguments[0].ToDisplayString();
                    resultTypeSymbol = returnType.TypeArguments[0] as INamedTypeSymbol;
                }

                bool hasResultKey = tempResources.Any(t => t.ResultKey is not null);

                if (hasResultKey && valueType is null)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE001",
                        $"Method '{method.Name}' has ResultKey on a write attribute but returns Task (not Task<T>). Skipping method."
                    ));
                    continue;
                }

                if (hasResultKey && valueType is not null && resultTypeSymbol is not null)
                {
                    bool valid = true;
                    foreach (var (_, _, rk) in tempResources)
                    {
                        if (rk is null)
                            continue;

                        var prop = resultTypeSymbol
                            .GetMembers()
                            .OfType<IPropertySymbol>()
                            .FirstOrDefault(p => p.Name == rk);

                        if (prop is null)
                        {
                            diagnostics.Add(new DiagnosticInfo(
                                "SLUICE001",
                                $"Method '{method.Name}': ResultKey property '{rk}' not found on type '{valueType}'. Skipping method."
                            ));
                            valid = false;
                            break;
                        }

                        var propType = prop.Type;
                        bool implementsIResourceKey = false;
                        if (resourceKeySymbol is not null && propType is INamedTypeSymbol propNamed)
                        {
                            implementsIResourceKey = propNamed.Equals(resourceKeySymbol, SymbolEqualityComparer.Default)
                                || propNamed.AllInterfaces.Any(iface =>
                                    iface is not null
                                    && SymbolEqualityComparer.Default.Equals(iface, resourceKeySymbol)
                                );
                        }

                        if (!implementsIResourceKey)
                        {
                            var propTypeName = prop.Type.ToDisplayString();
                            diagnostics.Add(new DiagnosticInfo(
                                "SLUICE001",
                                $"Method '{method.Name}': ResultKey property '{rk}' type '{propTypeName}' does not implement IResourceKey. Skipping method."
                            ));
                            valid = false;
                            break;
                        }
                    }

                    if (!valid)
                        continue;
                }

                bool hasColonResource = false;
                foreach (var (isCollection, name, resultKey) in tempResources)
                {
                    if (name.Contains(':'))
                    {
                        hasColonResource = true;
                        diagnostics.Add(new DiagnosticInfo(
                            "SLUICE005",
                            $"Method '{method.Name}': resource name '{name}' contains ':' which is not allowed (address delimiter)."
                        ));
                    }
                }

                if (hasColonResource)
                    continue;

                var resources = new List<ResourceAttribute>();
                foreach (var (isCollection, name, resultKey) in tempResources)
                {
                    if (name.Contains(':'))
                    {
                        continue;
                    }

                    if (resultKey is not null && resultTypeSymbol is not null)
                    {
                        var prop = resultTypeSymbol
                            .GetMembers()
                            .OfType<IPropertySymbol>()
                            .First(p => p.Name == resultKey);
                        var propType = prop.Type.ToDisplayString();
                        resources.Add(
                            new ResourceAttribute(
                                isCollection,
                                name,
                                ToIdentifier(name),
                                propType,
                                resultKey
                            )
                        );
                    }
                    else
                    {
                        resources.Add(
                            new ResourceAttribute(isCollection, name, ToIdentifier(name), keyType)
                        );
                    }
                }

                var extraParams = new List<string>();
                var extraNames = new List<string>();
                for (int i = 1; i < parameters.Length - 1; i++)
                {
                    extraParams.Add(parameters[i].Type.ToDisplayString());
                    extraNames.Add(parameters[i].Name);
                }

                writeMethods.Add(
                    new MethodData(
                        method.Name,
                        keyType,
                        valueType,
                        resources,
                        extraParams,
                        extraNames
                    )
                );
            }
        }

        var identifierToResourceName = new Dictionary<string, string>(StringComparer.Ordinal);
        var hasCollision = false;
        foreach (var rm in readMethods)
            foreach (var r in rm.Resources)
            {
                CheckCollision(r.FieldName, r.Name);
            }
        foreach (var wm in writeMethods)
            foreach (var r in wm.Resources)
            {
                CheckCollision(r.FieldName, r.Name);
            }

        void CheckCollision(string fieldName, string resourceName)
        {
            if (identifierToResourceName.TryGetValue(fieldName, out var existingName))
            {
                if (!StringComparer.Ordinal.Equals(existingName, resourceName))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        "SLUICE006",
                        $"Generated field identifier '{fieldName}' is a collision (from resource names '{existingName}' and '{resourceName}'). Use distinct resource names."
                    ));
                    hasCollision = true;
                }
            }
            else
            {
                identifierToResourceName[fieldName] = resourceName;
            }
        }

        if (hasCollision)
        {
            return new InterfaceData(
                interfaceName,
                finalClassName,
                namespaceStr,
                [],
                [],
                diagnostics
            );
        }

        if (readMethods.Count == 0 && writeMethods.Count == 0)
        {
            if (diagnostics.Count == 0)
                return null;

            return new InterfaceData(
                interfaceName,
                finalClassName,
                namespaceStr,
                readMethods,
                writeMethods,
                diagnostics
            );
        }

        return new InterfaceData(
            interfaceName,
            finalClassName,
            namespaceStr,
            readMethods,
            writeMethods,
            diagnostics
        );
    }

    private static void Emit(SourceProductionContext context, InterfaceData data)
    {
        foreach (var d in data.Diagnostics)
        {
            var descriptor = new DiagnosticDescriptor(
                d.Id,
                "Sluice",
                d.Message,
                "Sluice",
                DiagnosticSeverity.Warning,
                true
            );
            context.ReportDiagnostic(
                Diagnostic.Create(descriptor, location: null)
            );
        }

        if (data.ReadMethods.Count == 0 && data.WriteMethods.Count == 0)
            return;

        var interfaceName = data.InterfaceName;
        var className = data.GeneratedClassName;
        var resourcesName = $"{className}Resources";
        var sluiceName = $"{className}Sluice";

        var resourceSb = new StringBuilder();
        resourceSb.AppendLine("#pragma warning disable CS1591");
        resourceSb.AppendLine();
        resourceSb.AppendLine("using Sluice;");
        resourceSb.AppendLine();

        if (data.Namespace.Length > 0)
        {
            resourceSb.AppendLine($"namespace {data.Namespace}");
            resourceSb.AppendLine("{");
        }

        resourceSb.AppendLine($"public static class {resourcesName}");
        resourceSb.AppendLine("{");

        var allResources = new List<ResourceAttribute>();
        foreach (var rm in data.ReadMethods)
            foreach (var r in rm.Resources)
                if (!allResources.Any(x => x.Name == r.Name))
                    allResources.Add(r);

        foreach (var wm in data.WriteMethods)
            foreach (var r in wm.Resources)
                if (!allResources.Any(x => x.Name == r.Name))
                    allResources.Add(r);

        foreach (var res in allResources)
        {
            var kind = res.IsCollection ? "CollectionResource" : "EntityResource";
            var escapedName = res.Name.Replace("\"", "\\\"");
            resourceSb.AppendLine(
                $"    public static readonly {kind}<{res.KeyType}> {res.FieldName} = new(\"{escapedName}\");"
            );
        }

        resourceSb.AppendLine("}");
        resourceSb.AppendLine();

        if (data.Namespace.Length > 0)
        {
            resourceSb.AppendLine("}");
            resourceSb.AppendLine();
        }

        var sluiceSb = new StringBuilder();
        sluiceSb.AppendLine("#pragma warning disable CS1591");
        sluiceSb.AppendLine();
        sluiceSb.AppendLine("using Sluice;");
        sluiceSb.AppendLine();

        if (data.Namespace.Length > 0)
        {
            sluiceSb.AppendLine($"namespace {data.Namespace}");
            sluiceSb.AppendLine("{");
        }

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
                $"    public readonly TrackedRead<{keyType}, {valueType}> {fieldName} ="
            );
            sluiceSb.AppendLine(
                $"        {resourcesName}.{resourceField}.Read(store.{rm.MethodName});"
            );
            sluiceSb.AppendLine();
        }

        foreach (var wm in data.WriteMethods)
        {
            var fieldName = $"_{ToCamelCase(wm.MethodName)}";
            var keyType = wm.KeyType;
            var valueType = wm.ValueType;
            bool hasResult = valueType is not null;

            var staticResources = wm.Resources.Where(r => r.ResultKey is null).ToList();
            var resultResources = wm.Resources.Where(r => r.ResultKey is not null).ToList();

            var writeType = hasResult
                ? $"TrackedWrite<{keyType}, {valueType}>"
                : $"TrackedWrite<{keyType}>";

            sluiceSb.AppendLine($"    private readonly {writeType} {fieldName} = new(");
            sluiceSb.AppendLine($"        sluice,");

            var staticAddresses = staticResources.Select(r => $"{resourcesName}.{r.FieldName}.For");
            var hasResultResources = resultResources.Count > 0;
            if (staticAddresses.Any())
            {
                sluiceSb.AppendLine(
                    $"        [{string.Join(", ", staticAddresses)}]{(hasResultResources ? "," : "")}"
                );
            }
            else
            {
                sluiceSb.AppendLine($"        []{(hasResultResources ? "," : "")}");
            }

            for (int i = 0; i < resultResources.Count; i++)
            {
                var r = resultResources[i];
                var resolver = $"result => {resourcesName}.{r.FieldName}.For(result.{r.ResultKey})";
                sluiceSb.AppendLine(
                    $"        {resolver}{(i < resultResources.Count - 1 ? "," : "")}"
                );
            }

            sluiceSb.AppendLine("    );");
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

            var returnType = hasResult ? $"Task<{valueType}>" : "Task";
            sluiceSb.AppendLine(
                $"    public {returnType} {wm.MethodName}({keyType} id, {extraParamList}CancellationToken ct) =>"
            );
            sluiceSb.AppendLine(
                $"        {fieldName}.Write(id, ct => store.{wm.MethodName}(id, {extraCallList}ct), ct);"
            );
            sluiceSb.AppendLine();
        }

        sluiceSb.AppendLine("}");

        if (data.Namespace.Length > 0)
        {
            sluiceSb.AppendLine("}");
            sluiceSb.AppendLine();
        }

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
