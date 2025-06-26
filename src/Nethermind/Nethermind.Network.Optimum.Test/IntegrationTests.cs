// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test;

public class IntegrationTests
{
    // NOTE: These are sane defaults to use across all gRPC channels.
    private static readonly GrpcChannelOptions _grpcChannelOptions = new GrpcChannelOptions
    {
        Credentials = ChannelCredentials.Insecure,
        MaxReceiveMessageSize = int.MaxValue,
        MaxSendMessageSize = int.MaxValue,
        HttpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
            // TODO: For now we'll make this constants.
            KeepAlivePingDelay = TimeSpan.FromMinutes(2),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        }
    };

    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task GatewaySubscribeToTopic()
    {
        using var httpClient = new HttpClient() { BaseAddress = new Uri("http://localhost:8081") };
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:50051"), _grpcChannelOptions);

        var client = new GrpcOptimumGatewayClient(
            clientId: "nethermind-optimum-test-client",
            httpClient,
            grpcChannel);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var messages = client.SubscribeToTopic(topic: "test", threshold: 0.9, cts.Token);
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
    public async Task NodeSubscribeToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), _grpcChannelOptions);

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
    public async Task NodeSendToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), _grpcChannelOptions);

        var client = new GrpcOptimumNodeClient(grpcChannel);

        for (int i = 0; i < 10; i++)
        {
            byte[] data = Encoding.UTF8.GetBytes($"msg = {Guid.NewGuid()}");
            await client.SendToTopicAsync(topic: "test", data, CancellationToken.None);
        }

        await Task.CompletedTask;
    }

    [Test]
    public async Task NodeSendAndListenToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), _grpcChannelOptions);

        var client = new GrpcOptimumNodeClient(grpcChannel);
        var topic = "test";
        var messageCount = 10;

        var sender = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                byte[] data = Encoding.UTF8.GetBytes($"msg = {Guid.NewGuid()}");
                await client.SendToTopicAsync(topic, data, CancellationToken.None);
            }
        });

        var subscriber = Task.Run(async () =>
        {
            var messages = client.SubscribeToTopic(topic);
            var receivedCount = 0;
            await foreach (var _ in messages)
            {
                receivedCount++;

                if (receivedCount == messageCount)
                {
                    break;
                }
            }

            return receivedCount;
        });

        await Task.WhenAll(sender, subscriber);

        var receivedCount = subscriber.Result;
        receivedCount.Should().Be(messageCount);
    }
}
