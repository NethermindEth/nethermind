// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;

namespace Nethermind.Network.Optimum.Fuzzer;

public class Application(FuzzerOptions options)
{
    public Task RunAsync(CancellationToken token)
    {
        Console.WriteLine($"Options are: {options}");

        using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);

        return Task.CompletedTask;
    }
}
