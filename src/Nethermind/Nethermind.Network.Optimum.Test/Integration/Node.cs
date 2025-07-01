// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test.Integration;

public class Node
{
    private IContainer _container;
    private Uri _grpcEndpoint;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        using var p2pKeyStream = typeof(Node).Assembly.GetManifestResourceStream("identity/p2p.key");
        var identityFile = new byte[p2pKeyStream!.Length];
        p2pKeyStream.ReadExactly(identityFile, 0, identityFile.Length);

        _container = new ContainerBuilder()
            .WithImage("getoptimum/p2pnode:latest")
            .WithResourceMapping(identityFile, "/identity/p2p.key")
            .WithEnvironment("IDENTITY_DIR", "/identity")
            .WithEnvironment("NODE_MODE", "optimum")
            .WithEnvironment("SIDECAR_PORT", "33212") // Port for the grpc bidirectional streaming
            .WithEnvironment("API_PORT", "9090") // Port for the REST API
            .WithEnvironment("OPTIMUM_PORT", "7070") // Port for Optimum
            .WithPortBinding(33212, assignRandomHostPort: true)
            .WithPortBinding(9090, assignRandomHostPort: true)
            .WithPortBinding(7070, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r
                .ForPort(9090)
                .ForPath("/api/v1/health")))
            .Build();
        await _container.StartAsync();

        _grpcEndpoint = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(33212)}");
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _container.StopAsync();
        await _container.DisposeAsync();
    }

    [Test]
    public async Task PublishAndSubscribeToTopic()
    {
        using var grpcChannel = GrpcChannel.ForAddress(_grpcEndpoint, Configuration.DefaultGrpcChannelOptions);

        var client = new GrpcOptimumNodeClient(grpcChannel);
        var topic = Guid.NewGuid().ToString();
        var messageCount = 10;

        var subscriber = Task.Run(async () =>
        {
            var messages = client.SubscribeToTopic(topic);
            return await messages
                .Take(messageCount)
                .Select(msg => msg.Message)
                .ToArrayAsync();
        });

        var publisher = Task.Run(async () =>
        {
            var messages = Enumerable.Range(0, messageCount)
                .Select(_ => Encoding.UTF8.GetBytes($"msg = {Guid.NewGuid()}"))
                .ToArray();

            foreach (var msg in messages)
            {
                await client.PublishToTopicAsync(topic, msg, CancellationToken.None);
            }

            return messages;
        });

        var sentMessages = await publisher;
        var receivedMessages = await subscriber;
        receivedMessages.Should().BeEquivalentTo(sentMessages);
    }
}
