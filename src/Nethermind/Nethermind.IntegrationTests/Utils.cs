using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DotNet.Testcontainers.Containers;

namespace Nethermind.IntegrationTests;

public static class Utils
{
    public static async Task<string> GetCleanStdoutAsync(this IContainer container)
    {
        var (stdout, stderr) = await container.GetLogsAsync();
        
        // Strip ANSI escape codes that Nethermind uses for colored output
        return Regex.Replace(stdout, @"\e\[[0-9;]*m", string.Empty);
    }

    public static async Task<string> GetCleanStderrAsync(this IContainer container)
    {
        var (stdout, stderr) = await container.GetLogsAsync();
        
        // Strip ANSI escape codes that Nethermind uses for colored output
        return Regex.Replace(stderr, @"\e\[[0-9;]*m", string.Empty);
    }
}
