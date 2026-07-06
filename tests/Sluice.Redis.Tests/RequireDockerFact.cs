using System.Diagnostics;
using Xunit;

namespace Sluice.Redis.Tests;

public sealed class RequireDockerFact : FactAttribute
{
    public RequireDockerFact()
    {
        var psi = new ProcessStartInfo("docker", "info")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            var proc = Process.Start(psi);
            proc?.WaitForExit(5000);
            if (proc is null || proc.ExitCode != 0)
            {
                Skip = "Docker is not available";
                return;
            }
        }
        catch
        {
            Skip = "Docker is not available";
            return;
        }
    }
}
