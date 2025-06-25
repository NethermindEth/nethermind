// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Core.ServiceStopper;
using Nethermind.Grpc;
using Nethermind.Logging;
using Nethermind.Network;

namespace Nethermind.Runner.Ethereum
{
    public class GrpcRunner: IStoppableService
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
                Services = { NethermindService.BindService(_service) },
                Ports = { new ServerPort(_config.Host, _config.Port, ServerCredentials.Insecure) }
            };
            NetworkHelper.HandlePortTakenError(
                _server.Start, _config.Port
            );
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
