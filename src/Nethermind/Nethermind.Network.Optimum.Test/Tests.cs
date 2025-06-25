// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GetOptimum.Node.Proto;
using Grpc.Core;
using Grpc.Net.Client;
using NUnit.Framework;

namespace Nethermind.Network.Optimum.Test;

public class Tests
{
    [SetUp]
    public void Setup()
    {
    }

    [Test]
    public async Task GatewaySubscribeToTopic()
    {
        using var httpClient = new HttpClient();
        var client = new GrpcGatewayClient(httpClient, new GrpcGatewayClientOptions
        {
            ClientId = "nethermind-optimum-test-client",
            RestEndpoint = new Uri("http://localhost:8081/api/subscribe"),
            GrpcEndpoint = new Uri("http://localhost:50051")
        });

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
        using var httpClient = new HttpClient();

        using var channel = GrpcChannel.ForAddress(new Uri("http://localhost:33221"), new GrpcChannelOptions
        {
            Credentials = ChannelCredentials.Insecure,
            MaxReceiveMessageSize = int.MaxValue,
            MaxSendMessageSize = int.MaxValue,
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always,
                // TODO: For now we'll make this constants. Consider making them part of `GrpcGatewayClientOptions`
                KeepAlivePingDelay = TimeSpan.FromMinutes(2),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
            }
        });
        var client = new CommandStream.CommandStreamClient(channel);

        using var commands = client.ListenCommands();

        // TODO: We might want to encapsulate subscriptions into its own class
        await commands.RequestStream.WriteAsync(new ListenCommandsRequest
        {
            Command = ListenCommandsRequestType.SubscribeToTopic,
            Topic = "test",
        });
        await using var _ = Defer.Async(static async commands =>
        {
            await commands.RequestStream.WriteAsync(new ListenCommandsRequest
            {
                Command = ListenCommandsRequestType.UnsubscribeToTopic,
                Topic = "test",
            });
        }, commands);

        while (true)
        {
            ListenCommandsResponse current;
            try
            {
                await commands.ResponseStream.MoveNext().ConfigureAwait(false);
                current = commands.ResponseStream.Current;
            }
            catch (RpcException e) when (e.InnerException is OperationCanceledException)
            {
                throw e.InnerException;
            }

            switch (current.Command)
            {
                case ListenCommandsResponseType.Message:
                    var json = JsonSerializer.Deserialize<P2PNodeMessage>(current.Data.Span);
                    var asUtf8 = Encoding.UTF8.GetString(json!.Message);
                    break;
                // TODO: Figure out what to do with these cases
                case ListenCommandsResponseType.MessageTraceOptimumP2P:
                case ListenCommandsResponseType.MessageTraceGossipSub:
                case ListenCommandsResponseType.Unknown:
                default:
                    Console.WriteLine($"[DBG] Received unsupported command");
                    break;
            }
        }
    }
}

public static class Defer
{
    public static DeferAsyncDisposable<T> Async<T>(Func<T, Task> func, T ctx) => new(func, ctx);

    /// <remarks>
    /// Implemented as struct to avoid boxing, even under `IDisposable` usages.
    /// See: https://stackoverflow.com/a/2413844
    /// </remarks>
    public readonly struct DeferAsyncDisposable<T> : IAsyncDisposable
    {
        private readonly Func<T, Task> _func;
        private readonly T _ctx;

        public DeferAsyncDisposable(Func<T, Task> func, T ctx)
        {
            _func = func;
            _ctx = ctx;
        }

        public async ValueTask DisposeAsync() => await _func(_ctx);
    }
}

static class ListenCommandsRequestType
{
    public const int Unknown = 0;
    public const int PublishData = 1;
    public const int SubscribeToTopic = 2;
    public const int UnsubscribeToTopic = 3;
}

public sealed record P2PNodeMessage
{
    /// <summary>
    /// Topic name where the message was published.
    /// </summary>
    public required string Topic { get; init; }

    /// <summary>
    /// ID of the node that sent the message.
    /// </summary>
    public required string SourceNodeID { get; init; }

    /// <summary>
    /// Unique identifier for the message.
    /// </summary>
    // TODO: Contents do not look like valid UTF-8.
    // Also, message IDs are not unique, or based on the content of the `Message` itself (ex. like a hash)
    public required string MessageID { get; init; }

    /// <summary>
    /// Actual message data.
    /// </summary>
    public required byte[] Message { get; init; }
}
