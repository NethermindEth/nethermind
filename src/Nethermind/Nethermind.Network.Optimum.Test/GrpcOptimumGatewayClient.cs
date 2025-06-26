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
    IAsyncEnumerable<GatewayMessage> SubscribeToTopic(string topic, double threshold, CancellationToken cancellation = default);
}

public sealed class GrpcOptimumGatewayClient(
    string clientId,
    HttpClient httpClient,
    GrpcChannel grpcChannel
) : IOptimumGatewayClient
{
    public async IAsyncEnumerable<GatewayMessage> SubscribeToTopic(
        string topic,
        double threshold,
        [EnumeratorCancellation] CancellationToken token = default)
    {
        var subscribeRequest = new
        {
            client_id = clientId,
            topic,
            threshold
        };
        var subscribeResponse = await httpClient.SendAsync(new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri("api/subscribe", UriKind.Relative),
            Content = JsonContent.Create(subscribeRequest)
        }, token);
        subscribeResponse.EnsureSuccessStatusCode();

        var client = new GatewayStream.GatewayStreamClient(grpcChannel);

        using var call = client.ClientStream(cancellationToken: token);

        await call.RequestStream.WriteAsync(new GatewayMessage { ClientId = clientId }, token);

        // NOTE: Required due to limitations of the C# compiler.
        // See: https://github.com/dotnet/csharplang/issues/8414
        while (true)
        {
            GatewayMessage current;
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
