// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;


namespace Nethermind.Network.Optimum.Fuzzer;

public sealed class Application(FuzzerOptions options, ILogger logger)
{
    public Task RunAsync(CancellationToken token)
    {
        logger.LogInformation("{FuzzerOptions}", options);

        using var grpcChannel = GrpcChannel.ForAddress(options.GrpcEndpoint, Options.DefaultGrpcChannelOptions);

        return Task.CompletedTask;
    }
}
