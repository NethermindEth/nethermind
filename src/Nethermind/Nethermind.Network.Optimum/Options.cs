// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;

namespace Nethermind.Network.Optimum;

public static class Options
{
    // NOTE: These are sane defaults to use across all gRPC channels.
    public static readonly GrpcChannelOptions DefaultGrpcChannelOptions = new GrpcChannelOptions
    {
        Credentials = ChannelCredentials.Insecure,
        MaxReceiveMessageSize = int.MaxValue,
        MaxSendMessageSize = int.MaxValue,
        HttpHandler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
            InitialHttp2StreamWindowSize = 0x1000000,
            // TODO: For now we'll make this constants.
            KeepAlivePingDelay = TimeSpan.FromMinutes(5),
            KeepAlivePingTimeout = TimeSpan.FromSeconds(20),
        }
    };
}
