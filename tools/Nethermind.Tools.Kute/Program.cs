using System.Text.Json;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Tools.Kute.Auth;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.MetricsConsumer;
using Nethermind.Tools.Kute.SecretProvider;
using Nethermind.Tools.Kute.SystemClock;

namespace Nethermind.Tools.Kute;

static class Program
{
    public static async Task Main(string[] args)
    {
        await Parser.Default.ParseArguments<Config>(args).WithParsedAsync(async config =>
        {
            IServiceProvider serviceProvider = BuildServiceProvider(config);
            Application app = serviceProvider.GetService<Application>()!;

            await app.Run();
        });
    }

    static IServiceProvider BuildServiceProvider(Config config)
    {
        IServiceCollection collection = new ServiceCollection();

        collection.AddSingleton<Application>();
        collection.AddSingleton<ISystemClock, RealSystemClock>();
        collection.AddSingleton<HttpClient>();
        collection.AddSingleton<ISecretProvider>(new FileSecretProvider(config.JwtSecretFilePath));
        collection.AddSingleton<IAuth, JwtAuth>();
        collection.AddSingleton<IMessageProvider<JsonRpc?>>(
            new JsonRpcMessageProvider(
                new FileMessageProvider(config.MessagesFilePath))
        );
        collection.AddSingleton<IJsonRpcMethodFilter>(
            new ComposedJsonRpcMethodFilter(config.MethodFilters.Select(pattern =>
                new PatternJsonRpcMethodFilter(pattern)))
        );
        if (config.DryRun)
        {
            collection.AddSingleton<IJsonRpcSubmitter, NullJsonRpcSubmitter>();
        }
        else
        {
            collection.AddSingleton<IJsonRpcSubmitter>(provider =>
                new HttpJsonRpcSubmitter(
                    provider.GetRequiredService<HttpClient>(),
                    provider.GetRequiredService<IAuth>(),
                    config.HostAddress
                )
            );
        }

        switch (config.MetricConsumerStrategy)
        {
            case MetricConsumerStrategy.Report:
                collection.AddSingleton<IMetricsConsumer, PrettyReportMetricsConsumer>();
                break;
            case MetricConsumerStrategy.Json:
                collection.AddSingleton<IMetricsConsumer, JsonMetricsConsumer>();
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        return collection.BuildServiceProvider();
    }
}
