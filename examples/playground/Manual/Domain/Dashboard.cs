namespace Playground.Manual.Domain;

public sealed record Dashboard(User User, FeatureFlag Flag, Greeting? Greeting);
