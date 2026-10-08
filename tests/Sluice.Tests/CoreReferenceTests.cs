using System.Reflection;
using Microsoft.Extensions.DependencyModel;

namespace Sluice.Tests;

public sealed class CoreReferenceTests
{
    private static readonly string[] _dataAccessPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Data",
        "System.Data",
        "Npgsql",
        "Dapper",
    ];

    private static bool IsDataAccess(string name) =>
        _dataAccessPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    // T0
    [Fact]
    public void The_core_assembly_references_no_EF_or_data_access_library()
    {
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<AssemblyName>([typeof(SluiceCache).Assembly.GetName()]);
        while (pending.TryDequeue(out var name))
        {
            if (!closure.Add(name.Name!))
            {
                continue;
            }

            foreach (var reference in Assembly.Load(name).GetReferencedAssemblies())
            {
                pending.Enqueue(reference);
            }
        }

        closure
            .Should()
            .Contain(
                "ZiggyCreatures.FusionCache",
                "the walk must reach the core's own dependencies"
            );
        closure.Where(IsDataAccess).Should().BeEmpty();
    }

    // T0. The compiler drops unused references from assembly metadata, so the walk above misses a package
    // the core references but doesn't call; the package graph still carries it to every user.
    [Fact]
    public void The_core_package_graph_contains_no_EF_or_data_access_package()
    {
        var libraries = DependencyContext.Default!.RuntimeLibraries.ToDictionary(
            library => library.Name,
            StringComparer.OrdinalIgnoreCase
        );
        var closure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<string>(["Sluice"]);
        while (pending.TryDequeue(out var name))
        {
            if (!closure.Add(name) || !libraries.TryGetValue(name, out var library))
            {
                continue;
            }

            foreach (var dependency in library.Dependencies)
            {
                pending.Enqueue(dependency.Name);
            }
        }

        closure
            .Should()
            .Contain(
                "ZiggyCreatures.FusionCache",
                "the walk must reach the core's own dependencies"
            );
        closure.Where(IsDataAccess).Should().BeEmpty();
    }
}
