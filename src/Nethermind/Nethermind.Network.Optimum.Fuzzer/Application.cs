// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace Nethermind.Network.Optimum.Fuzzer;

public sealed class Application(FuzzerOptions options, ILogger logger)
{
    public async Task RunAsync(CancellationToken topLevelToken)
    {
        var topic = Guid.NewGuid().ToString();
        logger.LogDebug("Using topic {Topic}", topic);

        var seed = Random.Shared.Next();
        var random = new Random(seed);
        logger.LogDebug("Using seed {Seed}", seed);

        for (var run = 1; run <= options.Runs; run++)
        {
            logger.LogInformation("Run {RunNumber}/{TotalRuns}", run, options.Runs);
            using var cts = new CancellationTokenSource(options.Timeout);
            using var registration = topLevelToken.Register(cts.Cancel);
            try
            {
                await SingleRun(topic, random, cts.Token);
            }
            catch (OperationCanceledException) when (topLevelToken.IsCancellationRequested)
            {
                logger.LogInformation("Cancelled by user");
                return;
            }
            catch (Exception) when (cts.Token.IsCancellationRequested)
            {
                logger.LogError("Run {RunNumber} timed out after {Timeout} ms", run, options.Timeout.TotalMilliseconds);
            }
            catch (Exception e)
            {
                logger.LogError(e, "Run {RunNumber} failed", run);
            }
        }
    }

    private async Task SingleRun(string topic, Random random, CancellationToken token)
    {
        logger.LogDebug("Initializing {SubscriberCount} subscribers", options.SubscriberCount);
        Task<int>[] subscribers = Enumerable.Range(0, options.SubscriberCount)
            .Select(id => Task.Run(async () =>
            {
                logger.LogDebug("Initializing subscriber {Id}", id);
                using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);
                var client = new GrpcOptimumNodeClient(grpcChannel);

                var subscription = client.SubscribeToTopic(topic, token);
                var receivedMessages = 0;
                await foreach (var msg in subscription.Take(options.MessageCount * options.PublisherCount))
                {
                    logger.LogTrace("Subscriber {Id} received message: {Message}", id, msg);
                    receivedMessages++;
                }

                return receivedMessages;
            }))
            .ToArray();

        await Task.Delay(TimeSpan.FromSeconds(1), token);

        logger.LogDebug("Initializing {PublisherCount} publishers", options.PublisherCount);
        var publishers = Enumerable.Range(0, options.PublisherCount)
            .Select(id => Task.Run(async () =>
            {
                logger.LogDebug("Initializing publisher {Id}", id);
                using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);
                var client = new GrpcOptimumNodeClient(grpcChannel);

                var message = new byte[options.MessageSize];
                for (var i = 0; i < options.MessageCount; i++)
                {
                    random.NextBytes(message);

                    await client.PublishToTopicAsync(topic, message, CancellationToken.None);
                    logger.LogTrace("Publisher {Id} published message {Message}", id, message);

                    await Task.Delay(options.PublisherDelay, token);
                }

                logger.LogDebug("Publisher {Id} completed", id);
            }))
            .ToArray();

        await Task.WhenAll(subscribers);
        await Task.WhenAll(publishers);

        for (var i = 0; i < options.SubscriberCount; i++)
        {
            var receivedMessages = await subscribers[i];
            var expectedMessages = options.MessageCount * options.PublisherCount;
            if (receivedMessages != expectedMessages)
            {
                logger.LogError("Subscriber {Id} received {ReceivedMessages} messages, expected {ExpectedMessages}",
                    i, receivedMessages, expectedMessages);
            }
        }
    }
}
