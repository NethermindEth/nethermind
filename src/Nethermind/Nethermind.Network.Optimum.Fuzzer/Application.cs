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
    public async Task RunAsync(CancellationToken token)
    {
        var topic = Guid.NewGuid().ToString();
        logger.LogDebug("Using topic {Topic}", topic);

        var seed = Random.Shared.Next();
        var random = new Random(seed);
        logger.LogDebug("Using seed {Seed}", seed);

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
