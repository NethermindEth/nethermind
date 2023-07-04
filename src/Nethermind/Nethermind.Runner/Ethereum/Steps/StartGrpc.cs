// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Grpc;
using Nethermind.Grpc.Producers;
using Nethermind.Grpc.Servers;
using Nethermind.Init.Steps;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork))]
    public class StartGrpc : IStep
    {
        private readonly IApiWithNetwork _api;

        public StartGrpc(INethermindApi api)
        {
            _api = api;
        }

        public async Task Execute(CancellationToken cancellationToken)
        {
            IGrpcConfig grpcConfig = _api.Config<IGrpcConfig>();
            if (grpcConfig.Enabled)
            {
                ILogger logger = _api.LogManager.GetClassLogger();
                GrpcServer grpcServer = new(_api.EthereumJsonSerializer, _api.LogManager);
                GrpcRunner grpcRunner = new(grpcServer, grpcConfig, _api.LogManager);
                await grpcRunner.Start(cancellationToken).ContinueWith(x =>
                {
                    if (x.IsFaulted && logger.IsError)
                        logger.Error("Error during GRPC runner start", x.Exception);
                }, cancellationToken);

                _api.GrpcServer = grpcServer;

                GrpcPublisher grpcPublisher = new(_api.GrpcServer);
                _api.Publishers.Add(grpcPublisher);

                _api.DisposeStack.Push(grpcPublisher);

#pragma warning disable 4014
                _api.DisposeStack.Push(new Reactive.AnonymousDisposable(() => grpcRunner.StopAsync())); // do not await
#pragma warning restore 4014
            }
        }
    }
}
