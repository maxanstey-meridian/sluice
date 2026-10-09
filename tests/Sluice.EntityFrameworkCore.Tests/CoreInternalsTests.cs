using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// The EF package can see every core internal, but each one it uses is a contract the compiler doesn't hold across
/// packages, so it's held to one.
/// </summary>
public sealed class CoreInternalsTests
{
    private static readonly Assembly _efPackage = typeof(SluiceReadsExtensions).Assembly;
    private static readonly Assembly _core = typeof(SluiceCache).Assembly;

    [Fact]
    public void The_EF_package_uses_no_core_internal_but_ActiveCompute_Violate()
    {
        using var file = new PEReader(File.OpenRead(_efPackage.Location));
        var metadata = file.GetMetadataReader();
        var module = _efPackage.ManifestModule;

        var types = metadata
            .TypeReferences.Where(type => IsCore(metadata, type))
            .Select(type => module.ResolveType(MetadataTokens.GetToken(type)))
            .Where(type => !type.IsVisible)
            .Select(type => type.Name);
        var members = metadata
            .MemberReferences.Where(member =>
                IsCore(metadata, metadata.GetMemberReference(member).Parent)
            )
            .Select(member => module.ResolveMember(MetadataTokens.GetToken(member))!)
            .Where(member => !IsPublic(member))
            .Select(member => $"{member.DeclaringType!.Name}.{member.Name}");

        types
            .Concat(members)
            .Distinct()
            .Should()
            .BeEquivalentTo("ActiveCompute", "ActiveCompute.Violate");
    }

    // A type the core declares, referenced directly, nested, or as the generic type of an instantiation.
    private static bool IsCore(MetadataReader metadata, EntityHandle handle)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeReference:
                var scope = metadata.GetTypeReference((TypeReferenceHandle)handle).ResolutionScope;
                return scope.Kind is HandleKind.TypeReference
                    ? IsCore(metadata, (TypeReferenceHandle)scope)
                    : scope.Kind is HandleKind.AssemblyReference
                        && metadata.GetString(
                            metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name
                        ) == _core.GetName().Name;
            case HandleKind.TypeSpecification:
                var signature = metadata.GetBlobReader(
                    metadata.GetTypeSpecification((TypeSpecificationHandle)handle).Signature
                );
                if (signature.ReadSignatureTypeCode() is not SignatureTypeCode.GenericTypeInstance)
                {
                    return false;
                }

                signature.ReadSignatureTypeCode();
                return IsCore(metadata, signature.ReadTypeHandle());
            default:
                return false;
        }
    }

    private static bool IsPublic(MemberInfo member) =>
        member.DeclaringType!.IsVisible
        && member switch
        {
            MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
            FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
            _ => false,
        };
}
