// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using GetOptimum.Node.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace Nethermind.Network.Optimum.Test;

public sealed class GrpcOptimumNodeClient
{
    private readonly Uri _grpcEndpoint;

    public GrpcOptimumNodeClient(Uri grpcEndpoint)
    {
        _grpcEndpoint = grpcEndpoint;
    }

    private static class ListenCommandsRequestType
    {
        public const int Unknown = 0;
        public const int PublishData = 1;
        public const int SubscribeToTopic = 2;
        public const int UnsubscribeToTopic = 3;
    }

    public async IAsyncEnumerable<OptimumNodeMessage> SubscribeToTopic(
        string topic,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        using var channel = GrpcChannel.ForAddress(_grpcEndpoint, new GrpcChannelOptions
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

        using var commands = client.ListenCommands(cancellationToken: token);

        await commands.RequestStream.WriteAsync(new ListenCommandsRequest
        {
            Command = ListenCommandsRequestType.SubscribeToTopic,
            Topic = topic,
        }, token);

        while (true)
        {
            ListenCommandsResponse current;
            try
            {
                await commands.ResponseStream.MoveNext(token).ConfigureAwait(false);
                current = commands.ResponseStream.Current;
            }
            catch (RpcException e) when (e.InnerException is OperationCanceledException)
            {
                throw e.InnerException;
            }

            switch (current.Command)
            {
                case ListenCommandsResponseType.Message:
                    var json = JsonSerializer.Deserialize<OptimumNodeMessage>(current.Data.Span);
                    if (json is not null) yield return json;
                    break;
                // TODO: Figure out what to do with these cases
                case ListenCommandsResponseType.MessageTraceOptimumP2P:
                case ListenCommandsResponseType.MessageTraceGossipSub:
                case ListenCommandsResponseType.Unknown:
                default:
                    break;
            }
        }

        // TODO: We never send a `UnsubscribeToTopic` command to the Node
        // Discuss if this can be removed from the API and instead leverage gRPC to deal with that
    }
}
