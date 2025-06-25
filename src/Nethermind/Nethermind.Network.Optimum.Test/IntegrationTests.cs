// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test;

public class IntegrationTests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task GatewaySubscribeToTopic()
    {
        using var httpClient = new HttpClient() { BaseAddress = new Uri("http://localhost:8081") };
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:50051"), new GrpcChannelOptions
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
        });

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
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), new GrpcChannelOptions
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
        });

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

    // NOTE: The current implementation, both for Gateway and Node, uses a single gRPC connection per topic.
    // In an ideal scenario, we would multiplex multiple topics over a single gRPC connection.
    // The main benefit is to reduce the network load.
    // However, this has some drawbacks:
    // - The Gateway does not implement a mechanism to unsubscribe from a topic, so the same gRPC stream will continue sending messages from topic we might no longer be interested in.
    // - The Node implements a mechanism to unsubscribe from a topic, but managing topic from our side implies managing subscriptions. It can be done, but the implementation is more complex and potentially more fragile.
    //      For example, the gRPC library is not thread safe (https://learn.microsoft.com/en-us/aspnet/core/grpc/services?view=aspnetcore-9.0#multi-threading-with-grpc-streaming-methods), that is, we cannot read/write to the same gRPC stream from multiple threads.
    //
    // TODO: Ask how many topics are we interested in.
    // If the number is relatively low we can use multiple connections, otherwise we need to multiplex.
}
