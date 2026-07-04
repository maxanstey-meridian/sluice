namespace Sluice.Tests;

internal sealed record Customer(CustomerId Id, string Name, string Email);

internal sealed record Order(OrderId Id, CustomerId CustomerId, decimal Total);

internal sealed record CustomerScore(CustomerId CustomerId, int Score);

internal sealed record CustomerPatch(string? Name, string? Email);

internal sealed record CreateOrderInput(decimal Total);
