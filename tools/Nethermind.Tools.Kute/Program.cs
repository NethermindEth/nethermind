using App.Metrics.Formatters;
using App.Metrics.Formatters.Ascii;
using App.Metrics.Formatters.Json;
using CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Tools.Kute.Auth;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.MetricsConsumer;
using Nethermind.Tools.Kute.ProgressReporter;
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
        collection.AddSingleton<IAuth>(provider =>
            new TtlAuth(
                new JwtAuth(
                    provider.GetRequiredService<ISystemClock>(),
                    provider.GetRequiredService<ISecretProvider>()
                ),
                provider.GetRequiredService<ISystemClock>(),
                config.AuthTtl
            )
        );
        collection.AddSingleton<IMessageProvider<string>>(new FileMessageProvider(config.MessagesFilePath));
        collection.AddSingleton<IMessageProvider<JsonRpc?>, JsonRpcMessageProvider>();
        collection.AddSingleton<IJsonRpcValidator>(
            config.DryRun
                ? new NullJsonRpcValidator()
                : new NonErrorJsonRpcValidator()
        );
        collection.AddSingleton<IJsonRpcMethodFilter>(
            new ComposedJsonRpcMethodFilter(
                config.MethodFilters
                    .Select(pattern => new PatternJsonRpcMethodFilter(pattern))
                    .ToList()
            )
        );
        collection.AddSingleton<IJsonRpcSubmitter>(provider =>
            config.DryRun
                ? new NullJsonRpcSubmitter(provider.GetRequiredService<IAuth>())
                : new HttpJsonRpcSubmitter(
                    provider.GetRequiredService<HttpClient>(),
                    provider.GetRequiredService<IAuth>(),
                    config.HostAddress
                ));
        collection.AddSingleton<IProgressReporter>(provider =>
        {
            if (config.ShowProgress)
            {
                // TODO:
                // Terrible, terrible hack since it forces a double enumeration:
                // - A first one to count the number of messages.
                // - A second one to actually process each message.
                // We can reduce the cost by not parsing each message on the first enumeration
                // At the same time, this optimization relies on implementation details.
                var messagesProvider = provider.GetRequiredService<IMessageProvider<string>>();
                var totalMessages = messagesProvider.Messages.ToEnumerable().Count();
                return new ConsoleProgressReporter(totalMessages);
            }

            return new NullProgressReporter();
        });
        collection.AddSingleton<IMetricsConsumer, ConsoleMetricsConsumer>();
        collection.AddSingleton<IMetricsOutputFormatter>(
            config.MetricsOutputFormatter switch
            {
                MetricsOutputFormatter.Report => new MetricsTextOutputFormatter(),
                MetricsOutputFormatter.Json => new MetricsJsonOutputFormatter(),
                _ => throw new ArgumentOutOfRangeException(),
            }
        );

        return collection.BuildServiceProvider();
    }
}
