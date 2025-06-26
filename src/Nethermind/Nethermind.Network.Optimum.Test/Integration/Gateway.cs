// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test.Integration;

public class Gateway
{
    [Test]
    public async Task SubscribeToTopic()
    {
        using var httpClient = new HttpClient() { BaseAddress = new Uri("http://localhost:8081") };
        using var grpcChannel = GrpcChannel.ForAddress(new Uri("http://localhost:50051"), Configuration.DefaultGrpcChannelOptions);

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
}
