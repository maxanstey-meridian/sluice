namespace Sluice.Tests.GeneratorTests;

public sealed class GeneratorTests
{
    [Fact]
    public async Task Generated_Resource_Class_Has_Widget_Field()
    {
        WidgetStoreResources.Widget.Should().NotBeNull();
        WidgetStoreResources.Widget.Should().BeOfType<EntityResource<WidgetId>>();
    }

    [Fact]
    public async Task Generated_Resource_Address_Is_Correct()
    {
        var address = WidgetStoreResources.Widget.For(new WidgetId("w1"));
        address.Kind.Should().Be(ResourceKind.Entity);
        address.Name.Should().Be("widget");
        address.Key.Should().Be("w1");
    }

    [Fact]
    public async Task Generated_Collection_Resource_Has_Widgets_By_Group_Field()
    {
        WidgetStoreResources.WidgetsByGroup.Should().NotBeNull();
        WidgetStoreResources.WidgetsByGroup.Should().BeOfType<CollectionResource<WidgetId>>();
    }

    [Fact]
    public async Task Generated_Collection_Resource_Address_Is_Correct()
    {
        var address = WidgetStoreResources.WidgetsByGroup.For(new WidgetId("w1"));
        address.Kind.Should().Be(ResourceKind.Collection);
        address.Name.Should().Be("widgets.byGroup");
        address.Key.Should().Be("w1");
    }

    [Fact]
    public async Task Read_Through_Generated_Sluice_Tracks_Dependency()
    {
        var store = new FakeWidgetStore();
        var sluiceKernel = new SluiceKernel(new InMemoryCacheStore());
        var widgetSluice = new WidgetStoreSluice(sluiceKernel, store);

        var query = new CachedQuery<WidgetId, Widget>(
            "test.widget",
            async (id, scope) => await widgetSluice.Widget.Get(id, scope)
        );

        var result1 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        result1.Name.Should().Be("Widget1");
        store.GetWidgetCallCount.Should().Be(1);

        var result2 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        result2.Name.Should().Be("Widget1");
        store.GetWidgetCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Write_Through_Generated_Sluice_Invalidates_Cache()
    {
        var store = new FakeWidgetStore();
        var sluiceKernel = new SluiceKernel(new InMemoryCacheStore());
        var widgetSluice = new WidgetStoreSluice(sluiceKernel, store);

        var query = new CachedQuery<WidgetId, Widget>(
            "test.widget",
            async (id, scope) => await widgetSluice.Widget.Get(id, scope)
        );

        var result1 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        result1.Name.Should().Be("Widget1");
        store.GetWidgetCallCount.Should().Be(1);

        await widgetSluice.UpdateWidget(
            new WidgetId("w1"),
            new WidgetInput("Updated"),
            CancellationToken.None
        );
        store.UpdateWidgetCallCount.Should().Be(1);

        var result2 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        result2.Name.Should().Be("Updated");
        store.GetWidgetCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Collection_Read_Through_Generated_Sluice_Tracks_Dependency_And_Writes_Invalidates()
    {
        var store = new FakeWidgetStore();
        var sluiceKernel = new SluiceKernel(new InMemoryCacheStore());
        var widgetSluice = new WidgetStoreSluice(sluiceKernel, store);

        var query = new CachedQuery<WidgetId, IReadOnlyList<Widget>>(
            "test.widgetsByGroup",
            async (id, scope) => await widgetSluice.WidgetsByGroup.Get(id, scope)
        );

        var result1 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        result1.Single().Name.Should().Be("Widget1");
        store.GetWidgetsByGroupCallCount.Should().Be(1);

        await widgetSluice.UpdateWidgetsByGroup(
            new WidgetId("w1"),
            new WidgetInput("Updated"),
            CancellationToken.None
        );
        store.UpdateWidgetsByGroupCallCount.Should().Be(1);

        var result2 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        result2.Single().Name.Should().Be("Updated");
        store.GetWidgetsByGroupCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Write_With_ResultKey_Invalidates_Result_Derived_Address()
    {
        var store = new FakeWidgetStore();
        var sluiceKernel = new SluiceKernel(new InMemoryCacheStore());
        var widgetSluice = new WidgetStoreSluice(sluiceKernel, store);

        var query = new CachedQuery<WidgetId, Widget>(
            "test.widget",
            async (id, scope) => await widgetSluice.Widget.Get(id, scope)
        );

        var w2 = new WidgetId("w2");

        var result1 = await sluiceKernel.Get(query, w2, CancellationToken.None);
        result1.Name.Should().Be("Widget2");
        store.GetWidgetCallCount.Should().Be(1);

        var created = await widgetSluice.CreateWidget(
            new WidgetId("w2"),
            new WidgetInput("NewWidget"),
            CancellationToken.None
        );
        store.CreateWidgetCallCount.Should().Be(1);
        created.Id.Should().Be(w2);

        var result2 = await sluiceKernel.Get(query, w2, CancellationToken.None);
        result2.Name.Should().Be("NewWidget");
        store.GetWidgetCallCount.Should().Be(2);
    }

    [Fact]
    public async Task Generated_Sluice_Invalidate_Directly_Evicts_Cache()
    {
        var store = new FakeWidgetStore();
        var sluiceKernel = new SluiceKernel(new InMemoryCacheStore());
        var widgetSluice = new WidgetStoreSluice(sluiceKernel, store);

        var query = new CachedQuery<WidgetId, Widget>(
            "test.widget",
            async (id, scope) => await widgetSluice.Widget.Get(id, scope)
        );

        var result1 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        store.GetWidgetCallCount.Should().Be(1);

        await sluiceKernel.Invalidate(
            new WriteEffect(WidgetStoreResources.Widget.For(new WidgetId("w1"))),
            CancellationToken.None
        );

        var result2 = await sluiceKernel.Get(query, new WidgetId("w1"), CancellationToken.None);
        store.GetWidgetCallCount.Should().Be(2);
    }
}
