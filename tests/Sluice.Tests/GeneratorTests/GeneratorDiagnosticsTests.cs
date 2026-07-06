using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Sluice.Generators;

namespace Sluice.Tests.GeneratorTests;

public sealed class GeneratorDiagnosticsTests
{
    [Fact]
    public void ResultKey_Misuse_Emits_Diagnostics_And_Skips_Methods()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(WidgetId Id, string Name);

            [Sluice]
            public interface IBadWidgetStore
            {
                [WriteEntity("widget", ResultKey = nameof(Widget.Id))]
                Task UpdateWidget(WidgetId id, CancellationToken ct);

                [WriteEntity("widget", ResultKey = "Missing")]
                Task<Widget> CreateWidgetWithMissingResultKey(WidgetId id, CancellationToken ct);

                [WriteEntity("widget", ResultKey = nameof(Widget.Name))]
                Task<Widget> CreateWidgetWithNonKeyResult(WidgetId id, CancellationToken ct);
            }
            """
        );

        var diagnostics = runResult.Diagnostics;

        diagnostics
            .Should()
            .Contain(d =>
                d.Id == "SLUICE001"
                && d.GetMessage().Contains("UpdateWidget")
                && d.GetMessage().Contains("returns Task (not Task<T>)")
            );
        diagnostics
            .Should()
            .Contain(d =>
                d.Id == "SLUICE001"
                && d.GetMessage().Contains("CreateWidgetWithMissingResultKey")
                && d.GetMessage().Contains("ResultKey property 'Missing' not found")
            );
        diagnostics
            .Should()
            .Contain(d =>
                d.Id == "SLUICE001"
                && d.GetMessage().Contains("CreateWidgetWithNonKeyResult")
                && d.GetMessage().Contains("does not implement IResourceKey")
            );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Positional_Custom_Name_Emits_Class_With_NameSluice()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice("CustomWidget")]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Task<Widget> GetWidget(WidgetId id, CancellationToken ct);
            }
            """
        );

        var sources = runResult.GeneratedTrees;
        sources.Should().HaveCount(2);

        var sluiceSource = sources.First(t => t.FilePath.Contains("CustomWidgetSluice"));
        sluiceSource.GetText().ToString().Should().Contain("public sealed class CustomWidgetSluice");
    }

    [Fact]
    public void Custom_Name_Ending_In_Sluice_Emits_Diagnostic()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice("UserSluice")]
            public interface IUserStore
            {
                [ReadEntity("user")]
                Task<Widget> GetUser(WidgetId id, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE002"
            && d.GetMessage().Contains("UserSluice")
            && d.GetMessage().Contains("already ends with 'Sluice'")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Generated_Source_Is_Emitted_In_Interface_Namespace()
    {
        var runResult = RunGenerator(
            """
            namespace MyApp.Stores;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Task<Widget> GetWidget(WidgetId id, CancellationToken ct);
            }
            """
        );

        var sources = runResult.GeneratedTrees;
        sources.Should().HaveCount(2);

        var resourcesSource = sources.First(t => t.FilePath.Contains("WidgetStoreResources"));
        var resourcesText = resourcesSource.GetText().ToString();
        resourcesText.Should().Contain("namespace MyApp.Stores");
        resourcesText.Should().Contain("public static class WidgetStoreResources");

        var sluiceSource = sources.First(t => t.FilePath.Contains("WidgetStoreSluice"));
        var sluiceText = sluiceSource.GetText().ToString();
        sluiceText.Should().Contain("namespace MyApp.Stores");
        sluiceText.Should().Contain("public sealed class WidgetStoreSluice");
    }

    [Fact]
    public void Unsupported_Read_Return_Type_Emits_SLUICE003()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Widget GetWidget(WidgetId id, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE003"
            && d.GetMessage().Contains("GetWidget")
            && d.GetMessage().Contains("must return Task<T>")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Read_Missing_CancellationToken_Emits_SLUICE003()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Task<Widget> GetWidget(WidgetId id);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE003"
            && d.GetMessage().Contains("GetWidget")
            && d.GetMessage().Contains("CancellationToken")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Write_Missing_CancellationToken_Emits_SLUICE003()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [WriteEntity("widget")]
                Task UpdateWidget(WidgetId id, Widget input);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE003"
            && d.GetMessage().Contains("UpdateWidget")
            && d.GetMessage().Contains("CancellationToken")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Read_Extra_Parameters_Emits_SLUICE003()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Task<Widget> GetWidget(WidgetId id, int filter, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE003"
            && d.GetMessage().Contains("GetWidget")
            && d.GetMessage().Contains("exactly 2 parameters")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_Key_Type_Emits_SLUICE004()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Task<Widget> GetWidget(int id, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE004"
            && d.GetMessage().Contains("GetWidget")
            && d.GetMessage().Contains("int")
            && d.GetMessage().Contains("does not implement IResourceKey")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Invalid_Resource_Name_With_Colon_Emits_SLUICE005()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [WriteEntity("widget:invalid")]
                Task UpdateWidget(WidgetId id, Widget input, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE005"
            && d.GetMessage().Contains("widget:invalid")
            && d.GetMessage().Contains(":")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Read_Invalid_Resource_Name_With_Colon_Emits_SLUICE005()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget:invalid")]
                Task<Widget> GetWidget(WidgetId id, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE005"
            && d.GetMessage().Contains("widget:invalid")
            && d.GetMessage().Contains(":")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void ReadCollection_Invalid_Collection_Name_With_Colon_Emits_SLUICE005()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadCollection("widgets:invalid", "byGroup")]
                Task<IReadOnlyList<Widget>> GetWidgetsByGroup(WidgetId id, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE005"
            && d.GetMessage().Contains("widgets:invalid.byGroup")
            && d.GetMessage().Contains(":")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void ToIdentifier_Collision_Emits_SLUICE006()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);
            public sealed record WidgetInput(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("user-profile")]
                Task<Widget> GetUserProfile(WidgetId id, CancellationToken ct);

                [ReadEntity("userProfile")]
                Task<Widget> GetUserProfile2(WidgetId id, CancellationToken ct);
            }
            """
        );

        runResult.Diagnostics.Should().Contain(d =>
            d.Id == "SLUICE006"
            && d.GetMessage().Contains("UserProfile")
            && d.GetMessage().Contains("collision")
        );

        runResult.Results.Single().GeneratedSources.Should().BeEmpty();
    }

    [Fact]
    public void Method_With_Sluice_Attr_But_No_Read_Write_Skipped()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record WidgetId(string Value) : IResourceKey
            {
                public string ResourceKey => Value;
            }

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [Sluice("extra")]
                void SomeMethod(WidgetId id, CancellationToken ct);

                [ReadEntity("widget")]
                Task<Widget> GetWidget(WidgetId id, CancellationToken ct);
            }
            """
        );

        var sources = runResult.Results.Single().GeneratedSources;
        sources.Should().HaveCount(2);
    }

    [Fact]
    public void Exact_IResourceKey_Method_Parameter_Is_Accepted()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record Widget(string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [ReadEntity("widget")]
                Task<Widget> GetWidget(IResourceKey id, CancellationToken ct);
            }
            """
        );

        runResult.Results.Single().GeneratedSources.Should().HaveCount(2);
    }

    [Fact]
    public void Exact_IResourceKey_ResultKey_Property_Is_Accepted()
    {
        var runResult = RunGenerator(
            """
            namespace Sample;

            using System.Threading;
            using System.Threading.Tasks;
            using Sluice;

            public sealed record Widget(IResourceKey Id, string Name);

            [Sluice]
            public interface IWidgetStore
            {
                [WriteEntity("widget", ResultKey = nameof(Widget.Id))]
                Task<Widget> CreateWidget(IResourceKey id, Widget input, CancellationToken ct);
            }
            """
        );

        runResult.Results.Single().GeneratedSources.Should().HaveCount(2);
    }

    private static GeneratorDriverRunResult RunGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest)
        );
        var references = GetMetadataReferences().ToArray();
        var compilation = CSharpCompilation.Create(
            "GeneratorDiagnosticsTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
        );

        var generator = new SluiceGenerator();
        GeneratorDriver driver = CSharpGeneratorDriver.Create(generator.AsSourceGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out _);
        return driver.GetRunResult();
    }

    private static IEnumerable<MetadataReference> GetMetadataReferences()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (paths is not null)
        {
            foreach (var path in paths.Split(Path.PathSeparator))
            {
                if (seen.Add(path))
                {
                    yield return MetadataReference.CreateFromFile(path);
                }
            }
        }

        yield return MetadataReference.CreateFromFile(typeof(SluiceAttribute).Assembly.Location);
        yield return MetadataReference.CreateFromFile(typeof(SluiceGenerator).Assembly.Location);
    }
}
