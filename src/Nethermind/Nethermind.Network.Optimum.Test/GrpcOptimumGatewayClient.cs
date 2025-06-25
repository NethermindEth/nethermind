// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Threading;
using GetOptimum.Gateway.Proto;
using Grpc.Core;
using Grpc.Net.Client;

namespace Nethermind.Network.Optimum.Test;

public interface IOptimumGatewayClient
{
    IAsyncEnumerable<OptimumGatewayMessage> SubscribeToTopic(string topic, double threshold, CancellationToken cancellation = default);
}

public sealed record GrpcOptimumGatewayClientOptions
{
    public required string ClientId { get; init; }
    public required Uri RestEndpoint { get; init; }
    public required Uri GrpcEndpoint { get; init; }
}

public sealed class GrpcOptimumGatewayClient(
    HttpClient httpClient,
    GrpcOptimumGatewayClientOptions options
) : IOptimumGatewayClient
{
    public async IAsyncEnumerable<OptimumGatewayMessage> SubscribeToTopic(
        string topic,
        double threshold,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var subscribeRequest = new
        {
            client_id = options.ClientId,
            topic,
            threshold
        };
        var subscribeResponse = await httpClient.SendAsync(new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = options.RestEndpoint,
            Content = JsonContent.Create(subscribeRequest)
        }, token);
        subscribeResponse.EnsureSuccessStatusCode();

        using var channel = GrpcChannel.ForAddress(options.GrpcEndpoint, new GrpcChannelOptions
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
        var client = new GatewayStream.GatewayStreamClient(channel);

        using var call = client.ClientStream(cancellationToken: token);

        await call.RequestStream.WriteAsync(new OptimumGatewayMessage { ClientId = options.ClientId }, token);

        // NOTE: Required due to limitations of the C# compiler.
        // See: https://github.com/dotnet/csharplang/issues/8414
        while (true)
        {
            OptimumGatewayMessage current;
            try
            {
                await call.ResponseStream.MoveNext(token).ConfigureAwait(false);
                current = call.ResponseStream.Current;
            }
            catch (RpcException e) when (e.InnerException is OperationCanceledException)
            {
                throw e.InnerException;
            }

            yield return current;
        }
    }
}
