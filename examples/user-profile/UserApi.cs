namespace SluiceExample;

using Sluice;

public static class UserResources
{
    public static readonly EntityResource<UserId> User = new("user");

    public static readonly EntityResource<UserId> Settings = new("userSettings");

    public static readonly EntityResource<UserId> Preferences = new("userPreferences");
}

public sealed class UserSluice(ISluice sluice, IUserStore store)
{
    public readonly TrackedRead<UserId, User> User = UserResources.User.Read(store.GetUser);

    public readonly TrackedRead<UserId, UserSettings> Settings = UserResources.Settings.Read(
        store.GetSettings
    );

    public readonly TrackedRead<UserId, UserPreferences> Preferences =
        UserResources.Preferences.Read(store.GetPreferences);

    public readonly TrackedWrite<UserId> UpdateUser = new(
        sluice,
        UserResources.User.For,
        UserResources.Settings.For,
        UserResources.Preferences.For
    );
}

public sealed class UserQueries(UserSluice users)
{
    public readonly CachedQuery<UserId, User> UserById = new(
        "user.byId",
        id => id.Value,
        async (id, scope) => await users.User.Get(id, scope)
    );

    public readonly CachedQuery<UserId, UserProfile> Profile = new(
        "user.profile",
        id => id.Value,
        async (id, scope) =>
        {
            var u = await users.User.Get(id, scope);
            var s = await users.Settings.Get(id, scope);
            var p = await users.Preferences.Get(id, scope);

            return new UserProfile(id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
        }
    );
}

public sealed class UpdateUserUseCase(UserSluice users, IUserStore store)
{
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        users.UpdateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct);
}
