namespace UserProfile.Domain;

using Sluice;

public sealed record UserId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record GroupId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record OrderId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record User(UserId Id, string Name, string Email);

public sealed record UserSettings(UserId Id, bool DarkMode, string Language);

public sealed record UserPreferences(UserId Id, string Theme);

public sealed record UserProfile(
    UserId Id,
    string Name,
    string Email,
    bool DarkMode,
    string Language,
    string Theme
);

public sealed record Order(OrderId Id, UserId OwnerId, decimal Total);

public sealed record UpdateUserInput(string Name, bool DarkMode, string Theme);

public sealed record CreateUserInput(string Name, string Email);
