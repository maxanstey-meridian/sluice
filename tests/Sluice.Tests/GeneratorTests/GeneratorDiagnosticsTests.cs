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
