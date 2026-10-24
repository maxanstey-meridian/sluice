using System.Reflection;
using System.Reflection.Emit;

namespace Sluice;

/// <summary>
/// Which code a key format runs, so two declarations of one name can be told to agree: the format's method, compared
/// by module and metadata token. One lambda gives one method whatever it captures or whichever generic type holds it,
/// so it is the same format; two lambdas written separately are two, even when their text is the same.
/// </summary>
internal static class FormatIdentity
{
    // A compiled expression is a DynamicMethod, which has no token and is a new method on every compile, so it can't
    // be compared: null.
    internal static MethodInfo? Of(Delegate format) =>
        format.Method is DynamicMethod ? null : format.Method;

    internal static bool Same(MethodInfo a, MethodInfo b) =>
        a.Module.ModuleVersionId == b.Module.ModuleVersionId && a.MetadataToken == b.MetadataToken;

    internal static string Describe(MethodInfo format) =>
        format.DeclaringType is { } type
        && (type == typeof(KeyText) || type.DeclaringType == typeof(KeyText))
            ? "the default format, with no format given"
            : $"{format.DeclaringType}.{format.Name}";
}
