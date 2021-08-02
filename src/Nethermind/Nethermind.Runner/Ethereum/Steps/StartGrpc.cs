//  Copyright (c) 2021 Demerzel Solutions Limited
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
