namespace Sluice.Tests.GeneratorTests;

using Sluice;

public sealed record WidgetId(string Value) : IResourceKey
{
    public string ResourceKey => Value;
}

public sealed record Widget(WidgetId Id, string Name);

public sealed record WidgetInput(string Name);

[Sluice]
public interface IWidgetStore
{
    [ReadEntity("widget")]
    public Task<Widget> GetWidget(WidgetId id, CancellationToken ct);

    [ReadCollection("widgets", "byGroup")]
    public Task<IReadOnlyList<Widget>> GetWidgetsByGroup(WidgetId id, CancellationToken ct);

    [WriteEntity("widget")]
    public Task UpdateWidget(WidgetId id, WidgetInput input, CancellationToken ct);

    [WriteCollection("widgets", "byGroup")]
    public Task UpdateWidgetsByGroup(WidgetId id, WidgetInput input, CancellationToken ct);
}

public sealed class FakeWidgetStore : IWidgetStore
{
    public int GetWidgetCallCount { get; private set; }
    public int GetWidgetsByGroupCallCount { get; private set; }
    public int UpdateWidgetCallCount { get; private set; }
    public int UpdateWidgetsByGroupCallCount { get; private set; }

    private readonly Dictionary<WidgetId, Widget> _widgets = new()
    {
        [new WidgetId("w1")] = new Widget(new WidgetId("w1"), "Widget1"),
    };

    public Task<Widget> GetWidget(WidgetId id, CancellationToken ct)
    {
        GetWidgetCallCount++;
        return Task.FromResult(_widgets[id]);
    }

    public Task<IReadOnlyList<Widget>> GetWidgetsByGroup(WidgetId id, CancellationToken ct)
    {
        GetWidgetsByGroupCallCount++;
        return Task.FromResult<IReadOnlyList<Widget>>(
            _widgets.Values.OrderBy(widget => widget.Id.Value).ToArray()
        );
    }

    public Task UpdateWidget(WidgetId id, WidgetInput input, CancellationToken ct)
    {
        UpdateWidgetCallCount++;
        _widgets[id] = _widgets[id] with { Name = input.Name };
        return Task.CompletedTask;
    }

    public Task UpdateWidgetsByGroup(WidgetId id, WidgetInput input, CancellationToken ct)
    {
        UpdateWidgetsByGroupCallCount++;
        _widgets[id] = _widgets[id] with { Name = input.Name };
        return Task.CompletedTask;
    }
}
