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

public static class UserWriteEffects
{
    public static WriteEffect Updated(UserId id) =>
        WriteEffect
            .For()
            .Changes(UserResources.User.For(id))
            .Changes(UserResources.Settings.For(id))
            .Changes(UserResources.Preferences.For(id));
}

public sealed class UserQueries(IUserStore store)
{
    public readonly Query<UserId, User> User = new Query<UserId, User>("user.byId")
        .Key(id => id.Value)
        .Compute(
            async (id, read) =>
            {
                return await read.Track(UserResources.User.For(id), ct => store.GetUser(id, ct));
            }
        );

    public readonly Query<UserId, UserProfile> Profile = new Query<UserId, UserProfile>(
        "user.profile"
    )
        .Key(id => id.Value)
        .Compute(
            async (id, read) =>
            {
                var user = await read.Track(
                    UserResources.User.For(id),
                    ct => store.GetUser(id, ct)
                );

                var settings = await read.Track(
                    UserResources.Settings.For(id),
                    ct => store.GetSettings(id, ct)
                );

                var preferences = await read.Track(
                    UserResources.Preferences.For(id),
                    ct => store.GetPreferences(id, ct)
                );

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
