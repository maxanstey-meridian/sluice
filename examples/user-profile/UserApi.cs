namespace SluiceExample;

using Sluice;

public static class UserResources
{
    public static readonly EntityResource<UserId> User = Resource.Entity<UserId>("user");

    public static readonly EntityResource<UserId> Settings = Resource.Entity<UserId>(
        "userSettings"
    );

    public static readonly EntityResource<UserId> Preferences = Resource.Entity<UserId>(
        "userPreferences"
    );
}

public sealed class UserSluice
{
    public TrackedRead<UserId, User> User { get; }
    public TrackedRead<UserId, UserSettings> Settings { get; }
    public TrackedRead<UserId, UserPreferences> Preferences { get; }
    public Query<UserId, User> UserById { get; }
    public Query<UserId, UserProfile> Profile { get; }
    public TrackedWrite<UserId> UpdateUser { get; }

    private UserSluice(
        TrackedRead<UserId, User> user,
        TrackedRead<UserId, UserSettings> settings,
        TrackedRead<UserId, UserPreferences> preferences,
        Query<UserId, User> userById,
        Query<UserId, UserProfile> profile,
        TrackedWrite<UserId> updateUser
    )
    {
        User = user;
        Settings = settings;
        Preferences = preferences;
        UserById = userById;
        Profile = profile;
        UpdateUser = updateUser;
    }

    public static UserSluice Register(ISluice sluice, IUserStore store)
    {
        var user = new TrackedRead<UserId, User>(UserResources.User.For, store.GetUser);
        var settings = new TrackedRead<UserId, UserSettings>(
            UserResources.Settings.For,
            store.GetSettings
        );
        var preferences = new TrackedRead<UserId, UserPreferences>(
            UserResources.Preferences.For,
            store.GetPreferences
        );

        var userById = new Query<UserId, User>(
            "user.byId",
            id => id.Value,
            async (id, scope) => await user.Get(id, scope)
        );

        var profile = new Query<UserId, UserProfile>(
            "user.profile",
            id => id.Value,
            async (id, scope) =>
            {
                var u = await user.Get(id, scope);
                var s = await settings.Get(id, scope);
                var p = await preferences.Get(id, scope);

                return new UserProfile(id, u.Name, u.Email, s.DarkMode, s.Language, p.Theme);
            }
        );

        var updateUser = new TrackedWrite<UserId>(
            sluice,
            UserResources.User.For,
            UserResources.Settings.For,
            UserResources.Preferences.For
        );

        return new(user, settings, preferences, userById, profile, updateUser);
    }
}

public sealed class UpdateUserUseCase(UserSluice users, IUserStore store)
{
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        users.UpdateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct);
}
