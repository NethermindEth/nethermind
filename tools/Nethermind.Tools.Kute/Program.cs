using App.Metrics.Formatters;
using App.Metrics.Formatters.Ascii;
using App.Metrics.Formatters.Json;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Tools.Kute.Auth;
using Nethermind.Tools.Kute.FlowManager;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.MetricsConsumer;
using Nethermind.Tools.Kute.ProgressReporter;
using Nethermind.Tools.Kute.ResponseTracer;
using Nethermind.Tools.Kute.SecretProvider;
using Nethermind.Tools.Kute.SystemClock;
using System.CommandLine;

namespace Nethermind.Tools.Kute;

static class Program
{
    public static async Task<int> Main(string[] args)
    {
        RootCommand rootCommand =
        [
            Config.MessagesFilePath,
            Config.HostAddress,
            Config.JwtSecretFilePath,
            Config.AuthTtl,
            Config.DryRun,
            Config.ShowProgress,
            Config.MetricsOutputFormatter,
            Config.MethodFilters,
            Config.ResponsesTraceFile,
            Config.RequestsPerSecond,
            Config.UnwrapBatch
        ];
        rootCommand.SetAction((parseResult, cancellationToken) =>
        {
            IServiceProvider serviceProvider = BuildServiceProvider(parseResult);
            Application app = serviceProvider.GetService<Application>()!;

            return app.Run();
        });

        CommandLineConfiguration cli = new(rootCommand);

        return await cli.InvokeAsync(args);
    }

    private static IServiceProvider BuildServiceProvider(ParseResult parseResult)
    {
        bool dryRun = parseResult.GetValue(Config.DryRun);
        bool unwrapBatch = parseResult.GetValue(Config.UnwrapBatch);
        string? responsesTraceFile = parseResult.GetValue(Config.ResponsesTraceFile);
        IServiceCollection collection = new ServiceCollection();

        collection.AddSingleton<Application>();
        collection.AddSingleton<ISystemClock, RealSystemClock>();
        collection.AddSingleton<HttpClient>();
        collection.AddSingleton<ISecretProvider>(new FileSecretProvider(parseResult.GetValue(Config.JwtSecretFilePath)!));
        collection.AddSingleton<IAuth>(provider =>
            new TtlAuth(
                new JwtAuth(
                    provider.GetRequiredService<ISystemClock>(),
                    provider.GetRequiredService<ISecretProvider>()
                ),
                provider.GetRequiredService<ISystemClock>(),
                parseResult.GetValue(Config.AuthTtl)
            )
        );
        collection.AddSingleton<IMessageProvider<string>>(new FileMessageProvider(parseResult.GetValue(Config.MessagesFilePath)!));
        collection.AddSingleton<IMessageProvider<JsonRpc?>>(serviceProvider =>
        {
            var messageProvider = serviceProvider.GetRequiredService<IMessageProvider<string>>();
            var jsonMessageProvider = new JsonRpcMessageProvider(messageProvider);

            return unwrapBatch ? new UnwrapBatchJsonRpcMessageProvider(jsonMessageProvider) : jsonMessageProvider;
        });
        collection.AddSingleton<IJsonRpcValidator>(dryRun
            ? new NullJsonRpcValidator()
            : new ComposedJsonRpcValidator(new List<IJsonRpcValidator>
            {
                new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator(),
            })
        );
        collection.AddSingleton<IJsonRpcMethodFilter>(
            new ComposedJsonRpcMethodFilter(
                parseResult.GetValue(Config.MethodFilters)!
                    .Select(pattern => new PatternJsonRpcMethodFilter(pattern) as IJsonRpcMethodFilter)
                    .ToList()
            )
        );
        collection.AddSingleton<IJsonRpcSubmitter>(provider =>
        {
            if (!dryRun)
            {
                return new HttpJsonRpcSubmitter(
                    provider.GetRequiredService<HttpClient>(),
                    provider.GetRequiredService<IAuth>(),
                    parseResult.GetValue(Config.HostAddress)!
                );
            }

            // For dry runs we still want to trigger the generation of an AuthToken
            // This is to ensure that all parameters required for the generation are correct,
            // and not require a real run to verify that this is the case.
            string _ = provider.GetRequiredService<IAuth>().AuthToken;
            return new NullJsonRpcSubmitter();
        });
        collection.AddSingleton<IResponseTracer>(
            !dryRun && responsesTraceFile is not null
                ? new FileResponseTracer(responsesTraceFile)
                : new NullResponseTracer()
        );
        collection.AddSingleton<IProgressReporter>(provider =>
        {
            if (parseResult.GetValue(Config.ShowProgress))
            {
                // NOTE:
                // Terrible, terrible hack since it forces a double enumeration:
                // - A first one to count the number of messages.
                // - A second one to actually process each message.
                // We can reduce the cost by not parsing each message on the first enumeration
                // only when we're not unwrapping batches. If we are, we need to parse.
                // This optimization relies on implementation details.
                IMessageProvider<object?> messagesProvider = unwrapBatch
                    ? provider.GetRequiredService<IMessageProvider<JsonRpc?>>()
                    : provider.GetRequiredService<IMessageProvider<string>>();
                var totalMessages = messagesProvider.Messages.ToEnumerable().Count();
                return new ConsoleProgressReporter(totalMessages);
            }

            return new NullProgressReporter();
        });
        collection.AddSingleton<IMetricsConsumer, ConsoleMetricsConsumer>();
        collection.AddSingleton<IMetricsOutputFormatter>(
            parseResult.GetValue(Config.MetricsOutputFormatter) switch
            {
                MetricsOutputFormatter.Report => new MetricsTextOutputFormatter(),
                MetricsOutputFormatter.Json => new MetricsJsonOutputFormatter(),
                _ => throw new ArgumentOutOfRangeException(),
            }
        );
        collection.AddSingleton<IJsonRpcFlowManager>(new JsonRpcFlowManager(
            parseResult.GetValue(Config.RequestsPerSecond), unwrapBatch));

        return collection.BuildServiceProvider();
    }
}
