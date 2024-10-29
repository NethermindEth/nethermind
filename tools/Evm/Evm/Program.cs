using System.CommandLine;
using Evm.t8n;

namespace Evm;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var rootCmd = new RootCommand { Name = "Evm" };

        T8NCommand.Configure(ref rootCmd);

        await rootCmd.InvokeAsync(args);
    }
}
