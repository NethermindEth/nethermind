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

using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Grpc;
using Nethermind.Logging;

namespace Nethermind.Runner.Ethereum
{
    public class GrpcRunner
    {
        private readonly NethermindService.NethermindServiceBase _service;
        private readonly IGrpcConfig _config;
        private readonly ILogger _logger;
        private Server? _server;

        public GrpcRunner(NethermindService.NethermindServiceBase service, IGrpcConfig config, ILogManager logManager)
        {
            _service = service;
            _config = config;
            _logger = logManager.GetClassLogger();
        }

        public Task Start(CancellationToken cancellationToken)
        {
            _server = new Server
            {
                Services = {NethermindService.BindService(_service)},
                Ports = {new ServerPort(_config.Host, _config.Port, ServerCredentials.Insecure)}
            };
            _server.Start();
            if (_logger.IsInfo) _logger.Info($"Started GRPC server on {_config.Host}:{_config.Port}.");

            return Task.CompletedTask;
        }

        public async Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info("Stopping GRPC server...");
            await (_server?.ShutdownAsync() ?? Task.CompletedTask);
            await GrpcEnvironment.ShutdownChannelsAsync();
            if (_logger.IsInfo) _logger.Info("GRPC shutdown complete.");
        }
    }
}
