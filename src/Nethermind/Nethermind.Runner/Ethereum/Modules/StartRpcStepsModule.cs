// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.Api.Steps;
using Nethermind.Core;
using Nethermind.Core.PubSub;
using Nethermind.Grpc;
using Nethermind.Grpc.Producers;
using Nethermind.Grpc.Servers;
using Nethermind.Runner.Ethereum.Steps;

namespace Nethermind.Runner.Ethereum.Modules;

public class StartRpcStepsModule(IGrpcConfig grpcConfig) : Module
{
    protected override void Load(ContainerBuilder builder)
    {
        builder
            .AddStep(typeof(StartRpc));

        if (grpcConfig.Enabled)
        {

            builder
                .AddStep(typeof(StartGrpc))

                // Grpc server components.
                .AddSingleton<GrpcServer>()
                    .Bind<NethermindService.NethermindServiceBase, GrpcServer>()
                    .Bind<IGrpcServer, GrpcServer>()

                .AddSingleton<IPublisher, GrpcPublisher>()
                .AddSingleton<GrpcRunner>();
        }
    }
}
