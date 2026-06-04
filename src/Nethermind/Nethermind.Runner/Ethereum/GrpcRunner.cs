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
    public class GrpcRunner(NethermindService.NethermindServiceBase service, IGrpcConfig config, ILogManager logManager) : IStoppableService
    {
        private readonly NethermindService.NethermindServiceBase _service = service;
        private readonly IGrpcConfig _config = config;
        private readonly ILogger _logger = logManager.GetClassLogger<GrpcRunner>();
        private Server? _server;

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
            await (_server?.ShutdownAsync() ?? Task.CompletedTask);
            await GrpcEnvironment.ShutdownChannelsAsync();
            if (_logger.IsInfo) _logger.Info("GRPC shutdown complete.");
        }

        public string Description => "GRPC server";
    }
}
