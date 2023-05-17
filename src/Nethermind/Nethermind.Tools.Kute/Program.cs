using CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace Nethermind.Tools.Kute;

static class Program
{
    public static async Task Main(string[] args)
    {
        Config config = Parser.Default.ParseArguments<Config>(args).Value;
        IServiceProvider serviceProvider = BuildServiceProvider(config);
        Application app = serviceProvider.GetService<Application>()!;

        await app.Run();
    }

    static IServiceProvider BuildServiceProvider(Config config)
    {
        IServiceCollection collection = new ServiceCollection();

        collection.AddSingleton(config);
        collection.AddSingleton<Application>();
        collection.AddSingleton<ISystemClock, SystemClock>();
        collection.AddSingleton<IAuth, JwtAuth>();
        collection.AddSingleton<IJsonRpcMessageProvider, SingeFileJsonRpcMessageProvider>();
        if (config.DryRun)
        {
            collection.AddSingleton<IJsonRpcSubmitter, NullJsonRpcSubmitter>();
        }
        else
        {
            collection.AddSingleton<IJsonRpcSubmitter, HttpJsonRpcSubmitter>();
        }

        collection.AddSingleton<IMetricsConsumer, ConsoleMetricsConsumer>();

        return collection.BuildServiceProvider();
    }
}
