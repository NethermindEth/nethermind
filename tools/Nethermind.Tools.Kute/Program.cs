// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Microsoft.Extensions.DependencyInjection;
using Nethermind.Tools.Kute.AsyncProcessor;
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
            Config.ConcurrentRequests,
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
        bool showProgress = parseResult.GetValue(Config.ShowProgress);

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
        collection.AddSingleton<IMessageProvider<JsonRpc>>(serviceProvider =>
        {
            FileMessageProvider ofStrings = new FileMessageProvider(parseResult.GetValue(Config.MessagesFilePath)!);
            JsonRpcMessageProvider ofJsonRpc = new JsonRpcMessageProvider(ofStrings);
            IMessageProvider<JsonRpc> provider = unwrapBatch ? new UnwrapBatchJsonRpcMessageProvider(ofJsonRpc) : ofJsonRpc;

            return provider;
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
                _ => throw new ArgumentOutOfRangeException(nameof(Config.MetricsReportFormatter)),
            };

            MemoryMetricsReporter memoryReporter = new MemoryMetricsReporter();
            ConsoleTotalReporter consoleReporter = new ConsoleTotalReporter(memoryReporter, formatter);
            IMetricsReporter progresReporter = showProgress ? new ConsoleProgressReporter() : new NullMetricsReporter();

            return new ComposedMetricsReporter([memoryReporter, progresReporter, consoleReporter]);
        });
        collection.AddSingleton<IAsyncProcessor>(provider =>
        {
            int requestsPerSecond = parseResult.GetValue(Config.ConcurrentRequests);
            if (requestsPerSecond > 1)
            {
                return new ConcurrentProcessor(requestsPerSecond);
            }

            return new SequentialProcessor();
        });

        return collection.BuildServiceProvider();
    }
}
