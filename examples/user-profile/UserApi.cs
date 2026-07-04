namespace SluiceExample;

using Sluice;

public static class UserResources
{
    public static readonly EntityResource<UserId> User = Resource.Entity<UserId>("user");

    public static readonly EntityResource<UserId> Settings = Resource.Entity<UserId>(
        "userSettings"
    );
}

public sealed class UserQueries(IUserStore store)
{
    public readonly Query<UserId, User> User = new Query<UserId, User>("user.byId")
        .Key(id => id.Value)
        .Compute(
            async (id, read) =>
            {
                return await read.Track(UserResources.User.For(id), _ => store.GetUser(id));
            }
        );

    public readonly Query<UserId, UserProfile> Profile = new Query<UserId, UserProfile>(
        "user.profile"
    )
        .Key(id => id.Value)
        .Compute(
            async (id, read) =>
            {
                var user = await read.Track(UserResources.User.For(id), _ => store.GetUser(id));

                var settings = await read.Track(
                    UserResources.Settings.For(id),
                    _ => store.GetSettings(id)
                );

                return new UserProfile(
                    id,
                    user.Name,
                    user.Email,
                    settings.DarkMode,
                    settings.Language
                );
            }
        );
}

public sealed class UserCommands(ISluice sluice, IUserStore store)
{
    public Task UpdateUserName(UserId id, string name, CancellationToken ct) =>
        sluice.Apply(
            _ => store.UpdateUserName(id, name),
            changes => changes.Changed(UserResources.User.For(id)),
            ct
        );

    public Task UpdateDarkMode(UserId id, bool darkMode, CancellationToken ct) =>
        sluice.Apply(
            _ => store.UpdateDarkMode(id, darkMode),
            changes => changes.Changed(UserResources.Settings.For(id)),
            ct
        );
}
