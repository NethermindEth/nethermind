//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System.Reactive.Disposables;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Grpc;
using Nethermind.Grpc.Producers;
using Nethermind.Grpc.Servers;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork))]
    public class StartGrpc : IStep
    {
        private readonly INethermindApi _api;

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
                GrpcServer grpcServer = new GrpcServer(_api.EthereumJsonSerializer, _api.LogManager);
                var grpcRunner = new GrpcRunner(grpcServer, grpcConfig, _api.LogManager);
                await grpcRunner.Start(cancellationToken).ContinueWith(x =>
                {
                    if (x.IsFaulted && logger.IsError)
                        logger.Error("Error during GRPC runner start", x.Exception);
                });
            
                _api.GrpcServer = grpcServer;
                
                GrpcPublisher grpcPublisher = new GrpcPublisher(_api.GrpcServer);
                _api.Publishers.Add(grpcPublisher);
                
                _api.DisposeStack.Push(grpcPublisher);
                _api.DisposeStack.Push(Disposable.Create(() => grpcRunner.StopAsync())); // do not await
            }
        }
    }
}