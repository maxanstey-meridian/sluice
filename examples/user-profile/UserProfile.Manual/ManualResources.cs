namespace UserProfile.Manual;

using Sluice;
using UserProfile.Domain;

public static class ManualResources
{
    public static readonly EntityResource<UserId> User = new("user");

    public static readonly EntityResource<UserId> UserSettings = new("userSettings");

    public static readonly EntityResource<UserId> UserPreferences = new("userPreferences");

    public static readonly CollectionResource<GroupId> UsersByGroup = new("users.byGroup");

    public static readonly EntityResource<OrderId> Order = new("order");
}
