// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using GetOptimum.Node.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace Nethermind.Network.Optimum.Test;

public interface IOptimumNodeClient
{
    IAsyncEnumerable<OptimumNodeMessage> SubscribeToTopic(string topic, CancellationToken cancellation = default);
}

public sealed class GrpcOptimumNodeClient(
    GrpcChannel grpcChannel
) : IOptimumNodeClient
{
    internal static class ListenCommandsRequestType
    {
        internal const int Unknown = 0;
        internal const int PublishData = 1;
        internal const int SubscribeToTopic = 2;
        internal const int UnsubscribeToTopic = 3;
    }

    public async IAsyncEnumerable<OptimumNodeMessage> SubscribeToTopic(
        string topic,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var client = new CommandStream.CommandStreamClient(grpcChannel);

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
    }

    public Task SendToTopicAsync(string topic, ReadOnlySpan<byte> data, CancellationToken token = default)
    {
        var client = new CommandStream.CommandStreamClient(grpcChannel);
        using var commands = client.ListenCommands(cancellationToken: token);

        var request = new ListenCommandsRequest
        {
            Command = ListenCommandsRequestType.PublishData,
            Topic = topic,
            Data = Google.Protobuf.ByteString.CopyFrom(data)
        };

        return Send(client, request, token);

        static async Task Send(CommandStream.CommandStreamClient client, ListenCommandsRequest request, CancellationToken token)
        {
            try
            {
                await client.ListenCommands(cancellationToken: token)
                    .RequestStream.WriteAsync(request, token);
            }
            catch (RpcException e) when (e.InnerException is OperationCanceledException)
            {
                throw e.InnerException;
            }
        }
    }
}
