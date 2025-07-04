// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GetOptimum.Node.Proto;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Nethermind.Network.Optimum.Fuzzer;

public sealed class Application(FuzzerOptions options, ILogger logger)
{
    public async Task RunAsync(CancellationToken topLevelToken)
    {
        var seed = Random.Shared.Next();
        var random = new Random(seed);
        logger.LogDebug("Using seed {Seed}", seed);

        for (var run = 1; run <= options.Runs; run++)
        {
            using (logger.BeginScope("Run {Run}/{TotalRuns}", run, options.Runs))
            {
                using var cts = new CancellationTokenSource(options.Timeout);
                using var registration = topLevelToken.Register(cts.Cancel);

                var topic = Guid.NewGuid().ToString();
                logger.LogDebug("Using topic {Topic}", topic);

                var report = new RunReport(options.SubscriberCount, options.PublisherCount);
                try
                {
                    if (options.TouchTopic)
                    {
                        logger.LogDebug("Touching topic {Topic}", topic);
                        await TouchTopicAsync(topic, cts.Token);
                    }

                    await SingleRun(report, topic, random, cts.Token);
                }
                catch (OperationCanceledException) when (topLevelToken.IsCancellationRequested)
                {
                    logger.LogInformation("Cancelled by user");
                    return;
                }
                catch (Exception) when (cts.Token.IsCancellationRequested)
                {
                    logger.LogError("Timed out after {Timeout} ms", options.Timeout.TotalMilliseconds);
                    report.Status = new ReportStatus.TimedOut(timeout: options.Timeout);
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Failed");
                    report.Status = new ReportStatus.Failed(e);
                }

                Console.WriteLine($"=== Run {run}/{options.Runs} ===");
                Console.WriteLine($"* Status: {report.Status}");
                Console.WriteLine($"* Topic: '{topic}'");
                Console.WriteLine($"* Subscribers ({options.SubscriberCount}):");
                foreach (var subscriber in report.Subscribers)
                {
                    Console.Write($"    {subscriber.Id} => {subscriber.Messages} messages");
                    if (subscriber.RunningTime.HasValue)
                    {
                        Console.WriteLine($" / {subscriber.RunningTime.Value.TotalMilliseconds} ms");
                    }
                    else
                    {
                        Console.WriteLine(" (DNC)");
                    }
                }
                Console.WriteLine($"* Publishers ({options.PublisherCount}):");
                foreach (var publisher in report.Publishers)
                {
                    Console.Write($"    {publisher.Id} => {publisher.Messages} messages");
                    if (publisher.RunningTime.HasValue)
                    {
                        Console.WriteLine($" / {publisher.RunningTime.Value.TotalMilliseconds} ms");
                    }
                    else
                    {
                        Console.WriteLine(" (DNC)");
                    }
                }
            }
        }
    }

    private async Task SingleRun(RunReport run, string topic, Random random, CancellationToken token)
    {
        logger.LogDebug("Initializing {SubscriberCount} subscribers", options.SubscriberCount);
        var subscribers = Enumerable.Range(0, options.SubscriberCount)
            .Select(id => Task.Run(async () =>
            {
                using (logger.BeginScope("Subscriber {Id}", id))
                {
                    logger.LogDebug("Initializing");
                    var report = run.Subscribers[id];

                    using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);
                    var client = new GrpcOptimumNodeClient(grpcChannel);

                    var expectedMessages = options.MessageCount * options.PublisherCount;
                    var subscription = client.SubscribeToTopic(topic, token);

                    var startTime = 0L;
                    await foreach (var msg in subscription.Take(expectedMessages))
                    {
                        if (report.Messages == 0) { startTime = Stopwatch.GetTimestamp(); }

                        logger.LogTrace("Received message: {Message}", msg);
                        report.Messages++;
                    }
                    report.RunningTime = Stopwatch.GetElapsedTime(startTime);

                    if (report.Messages != expectedMessages)
                    {
                        logger.LogError("Received {ReceivedMessages} messages, expected {ExpectedMessages}", report.Messages, expectedMessages);
                        throw new ClientException($"Subscriber {id} received an unexpected number of messages");
                    }
                    else
                    {
                        logger.LogDebug("Completed with {ReceivedMessages} messages", report.Messages);
                    }
                }
            }))
            .ToArray();

        await Task.Delay(TimeSpan.FromSeconds(1), token);

        logger.LogDebug("Initializing {PublisherCount} publishers", options.PublisherCount);
        var publishers = Enumerable.Range(0, options.PublisherCount)
            .Select(id => Task.Run(async () =>
            {
                using (logger.BeginScope("Publisher {Id}", id))
                {
                    logger.LogDebug("Initializing");
                    var report = run.Publishers[id];

                    using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);
                    var client = new GrpcOptimumNodeClient(grpcChannel);

                    var message = new byte[options.MessageSize];

                    var startTime = Stopwatch.GetTimestamp();
                    for (var i = 0; i < options.MessageCount; i++)
                    {
                        random.NextBytes(message);

                        await client.PublishToTopicAsync(topic, message, CancellationToken.None);
                        report.Messages++;
                        logger.LogTrace("Published message: {Message}", message);

                        await Task.Delay(options.PublisherDelay, token);
                    }
                    report.RunningTime = Stopwatch.GetElapsedTime(startTime);

                    logger.LogDebug("Completed with {PublishedMessages} messages", options.MessageCount);
                }
            }))
            .ToArray();

        await Task.WhenAll(subscribers);
        await Task.WhenAll(publishers);
    }

    /// <remarks>
    /// Intended to be used to ensure the topic exists before running the fuzzer.
    /// A workaround that helps to avoid issues in the Optimum node due to concurrent topic creation.
    /// </remarks>
    private async Task TouchTopicAsync(string topic, CancellationToken token)
    {
        using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);
        var client = new CommandStream.CommandStreamClient(grpcChannel);
        using var commands = client.ListenCommands(cancellationToken: token);

        await commands.RequestStream.WriteAsync(new ListenCommandsRequest
        {
            Command = 2,
            Topic = topic,
        }, token);
    }
}
