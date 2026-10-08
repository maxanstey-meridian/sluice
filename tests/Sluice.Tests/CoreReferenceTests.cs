using System.Reflection;

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
        closure
            .Where(name =>
                _dataAccessPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
            )
            .Should()
            .BeEmpty();
    }
}
