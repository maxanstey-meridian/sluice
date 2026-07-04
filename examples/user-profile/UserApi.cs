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

public sealed class UserReads(IUserStore store)
{
    public readonly TrackedRead<UserId, User> User = new(
        UserResources.User.For,
        (id, ct) => store.GetUser(id, ct)
    );

    public readonly TrackedRead<UserId, UserSettings> Settings = new(
        UserResources.Settings.For,
        (id, ct) => store.GetSettings(id, ct)
    );

    public readonly TrackedRead<UserId, UserPreferences> Preferences = new(
        UserResources.Preferences.For,
        (id, ct) => store.GetPreferences(id, ct)
    );
}

public static class UserWriteEffects
{
    public static WriteEffect Updated(UserId id) =>
        new(
            UserResources.User.For(id),
            UserResources.Settings.For(id),
            UserResources.Preferences.For(id)
        );
}

public sealed class UserQueries(UserReads reads)
{
    public readonly Query<UserId, User> User = new(
        "user.byId",
        id => id.Value,
        async (id, scope) =>
        {
            return await reads.User.Get(id, scope);
        }
    );

    public readonly Query<UserId, UserProfile> Profile = new(
        "user.profile",
        id => id.Value,
        async (id, scope) =>
        {
            var user = await reads.User.Get(id, scope);
            var settings = await reads.Settings.Get(id, scope);
            var preferences = await reads.Preferences.Get(id, scope);

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
}

public sealed class UpdateUserUseCase(ISluice sluice, IUserStore store)
{
    public Task Execute(UserId id, UpdateUserInput input, CancellationToken ct) =>
        sluice.Apply(ct => store.UpdateUser(id, input, ct), UserWriteEffects.Updated(id), ct);
}
