namespace Sluice;

internal static class ResourceKeyGuard
{
    public static string RequireRealKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException(
                "Resource key must not be empty or whitespace.",
                nameof(key)
            );
        }
        if (key.Contains(':'))
        {
            throw new ArgumentException("Resource key must not contain ':'.", nameof(key));
        }
        if (key == "*")
        {
            throw new ArgumentException(
                "Resource key must not be '*'; it is reserved for wildcard addresses.",
                nameof(key)
            );
        }
        return key;
    }
}
