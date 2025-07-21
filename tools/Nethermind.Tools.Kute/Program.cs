// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Tools.Kute.Auth;
using Nethermind.Tools.Kute.JsonRpcMethodFilter;
using Nethermind.Tools.Kute.JsonRpcSubmitter;
using Nethermind.Tools.Kute.JsonRpcValidator;
using Nethermind.Tools.Kute.JsonRpcValidator.Eth;
using Nethermind.Tools.Kute.MessageProvider;
using Nethermind.Tools.Kute.Metrics;
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
            Config.ShowProgress,
            Config.MetricsReportFormatter,
            Config.MethodFilters,
            Config.ResponsesTraceFile,
            Config.RequestsPerSecond,
            Config.UnwrapBatch
        ];
        rootCommand.SetAction(async (parseResult, cancellationToken) =>
        {
            IServiceProvider serviceProvider = BuildServiceProvider(parseResult);
            Application app = serviceProvider.GetService<Application>()!;

            await app.Run(cancellationToken);
        });

        CommandLineConfiguration cli = new(rootCommand);

        return await cli.InvokeAsync(args);
    }

    private static IServiceProvider BuildServiceProvider(ParseResult parseResult)
    {
        bool unwrapBatch = parseResult.GetValue(Config.UnwrapBatch);
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
                TimeSpan.FromSeconds(parseResult.GetValue(Config.AuthTtl))
            )
        );
        collection.AddSingleton<IMessageProvider<string>>(
            new FileMessageProvider(parseResult.GetValue(Config.MessagesFilePath)!));
        collection.AddSingleton<IMessageProvider<JsonRpc>>(serviceProvider =>
        {
            var messageProvider = new FileMessageProvider(parseResult.GetValue(Config.MessagesFilePath)!);
            var jsonMessageProvider = new JsonRpcMessageProvider(messageProvider);

            return unwrapBatch ? new UnwrapBatchJsonRpcMessageProvider(jsonMessageProvider) : jsonMessageProvider;
        });
        collection.AddSingleton<IJsonRpcValidator>(
            new ComposedJsonRpcValidator(
                [new NonErrorJsonRpcValidator(), new NewPayloadJsonRpcValidator()]));
        collection.AddSingleton<IJsonRpcMethodFilter>(
            new ComposedJsonRpcMethodFilter(
                [..parseResult
                    .GetValue(Config.MethodFilters)!
                    .Select(pattern => new PatternJsonRpcMethodFilter(pattern) as IJsonRpcMethodFilter)]
            )
        );
        collection.AddSingleton<IJsonRpcSubmitter>(provider =>
            new HttpJsonRpcSubmitter(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<IAuth>(),
                parseResult.GetValue(Config.HostAddress)!
            ));
        collection.AddSingleton<IResponseTracer>(_ =>
        {
            string? tracesFilePath = parseResult.GetValue(Config.ResponsesTraceFile);
            if (tracesFilePath is null)
            {
                return new NullResponseTracer();
            }

            return new FileResponseTracer(tracesFilePath);
        });
        collection.AddSingleton<IMetricsReporter>(provider =>
        {
            IMetricsReportFormatter formatter = parseResult.GetValue(Config.MetricsReportFormatter) switch
            {
                MetricsReportFormat.Pretty => new PrettyMetricsReportFormatter(),
                MetricsReportFormat.Json => new JsonMetricsReportFormatter(),
                _ => throw new ArgumentOutOfRangeException(),
            };

            var memoryReporter = new MemoryMetricsReporter();
            var consoleReporter = new ConsoleTotalReporter(memoryReporter, formatter);

            IMetricsReporter progresReporter;
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
                    ? provider.GetRequiredService<IMessageProvider<JsonRpc>>()
                    : provider.GetRequiredService<IMessageProvider<string>>();
                var totalMessages = messagesProvider.Messages().ToEnumerable().Count();

                progresReporter = new ConsoleProgressReporter(totalMessages);
            }
            else
            {
                progresReporter = new NullMetricsReporter();
            }

            return new ComposedMetricsReporter([memoryReporter, progresReporter, consoleReporter]);
        });

        return collection.BuildServiceProvider();
    }
}
