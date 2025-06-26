// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test.Integration;

public class Node
{
    [Test]
    public async Task SubscribeToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), Configuration.DefaultGrpcChannelOptions);

        var client = new GrpcOptimumNodeClient(grpcChannel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var messages = client.SubscribeToTopic(topic: "test", cts.Token);
        int receivedCount = 0;
        try
        {
            await foreach (var _ in messages)
            {
                receivedCount++;
            }
        }
        catch (OperationCanceledException) { }

        receivedCount.Should().BeGreaterThan(0, "Expected to receive at least one message from the gateway");
    }

    [Test]
    public async Task PublishToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), Configuration.DefaultGrpcChannelOptions);

        var client = new GrpcOptimumNodeClient(grpcChannel);

        for (int i = 0; i < 10; i++)
        {
            byte[] data = Encoding.UTF8.GetBytes($"msg = {Guid.NewGuid()}");
            await client.PublishToTopicAsync(topic: "test", data, CancellationToken.None);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task PublishAndSubscribeToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), Configuration.DefaultGrpcChannelOptions);

        var client = new GrpcOptimumNodeClient(grpcChannel);
        var topic = "test";

        var sentMessages = Enumerable.Range(0, 10)
            .Select(_ => Encoding.UTF8.GetBytes($"msg = {Guid.NewGuid()}"))
            .ToArray();

        var publisher = Task.Run(async () =>
        {
            foreach (var msg in sentMessages)
            {
                await client.PublishToTopicAsync(topic, msg, CancellationToken.None);
            }
        });

        var subscriber = Task.Run(async () =>
        {
            var messages = client.SubscribeToTopic(topic);
            return await messages
                .Take(sentMessages.Length)
                .Select(msg => msg.Message)
                .ToArrayAsync();
        });

        await Task.WhenAll(publisher, subscriber);

        var receivedMessages = subscriber.Result;
        receivedMessages.Should().BeEquivalentTo(sentMessages);
    }
}
