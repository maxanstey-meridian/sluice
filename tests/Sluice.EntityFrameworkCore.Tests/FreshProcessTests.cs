using System.Diagnostics;
using Sluice.EntityFrameworkCore.Tests.Support;

namespace Sluice.EntityFrameworkCore.Tests;

/// <summary>
/// What happens once per process, which no test in this one can see: every test here has subscribed already. Each
/// runs a program built beside the tests in a process of its own.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class FreshProcessTests(PostgresFixture postgres)
{
    // The program says which step failed (Sluice.EntityFrameworkCore.FreshProcess/Program.cs).
    [Fact]
    public async Task The_foreign_context_check_starts_at_AddSluiceEntityFrameworkCore_and_covers_listeners_made_before_it()
    {
        var start = new ProcessStartInfo("dotnet")
        {
            ArgumentList =
            {
                Path.Combine(
                    AppContext.BaseDirectory,
                    "Sluice.EntityFrameworkCore.FreshProcess.dll"
                ),
                postgres.ConnectionString,
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        (process.ExitCode, await output, await error)
            .Should()
            .Be(
                (
                    0,
                    $"not refused before the call{Environment.NewLine}refused after the call{Environment.NewLine}",
                    ""
                )
            );
    }
}
