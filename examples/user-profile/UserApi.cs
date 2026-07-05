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
    public readonly TrackedRead<UserId, User> User;
    public readonly TrackedRead<UserId, UserSettings> Settings;
    public readonly TrackedRead<UserId, UserPreferences> Preferences;
    public readonly Query<UserId, User> UserById;
    public readonly Query<UserId, UserProfile> Profile;
    public readonly TrackedWrite<UserId> UpdateUser;

    public UserSluice(ISluice sluice, IUserStore store)
    {
        User = new(
            UserResources.User.For,
            (id, ct) => store.GetUser(id, ct)
        );
        Settings = new(
            UserResources.Settings.For,
            (id, ct) => store.GetSettings(id, ct)
        );
        Preferences = new(
            UserResources.Preferences.For,
            (id, ct) => store.GetPreferences(id, ct)
        );

        UserById = new(
            "user.byId",
            id => id.Value,
            async (id, scope) => await User.Get(id, scope)
        );

        Profile = new(
            "user.profile",
            id => id.Value,
            async (id, scope) =>
            {
                var user = await User.Get(id, scope);
                var settings = await Settings.Get(id, scope);
                var preferences = await Preferences.Get(id, scope);

                return new UserProfile(
                    id,
                    user.Name,
                    user.Email,
                    settings.DarkMode,
                    settings.Language,
                    preferences.Theme
                );
            }
        );

        UpdateUser = new(
            sluice,
            UserResources.User.For,
            UserResources.Settings.For,
            UserResources.Preferences.For
        );
    }
}

public sealed class UpdateUserUseCase(UserSluice users, IUserStore store)
{
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        users.UpdateUser.Write(id, ct => store.UpdateUser(id, input, ct), ct);
}
