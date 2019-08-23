using System.Threading.Tasks;
using Grpc.Core;
using Nethermind.Grpc;
using Nethermind.Logging;

namespace Nethermind.Runner.Runners
{
    public class GrpcRunner : IRunner
    {
        private readonly NethermindService.NethermindServiceBase _service;
        private readonly IGrpcConfig _config;
        private readonly ILogger _logger;
        private Server _server;

        public GrpcRunner(NethermindService.NethermindServiceBase service, IGrpcConfig config, ILogManager logManager)
        {
            _service = service;
            _config = config;
            _logger = logManager.GetClassLogger();
        }

        public Task Start()
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
            if (_logger.IsInfo) _logger.Info($"Stopping GRPC server...");
            await _server.ShutdownAsync();
            await GrpcEnvironment.ShutdownChannelsAsync();
            if (_logger.IsInfo) _logger.Info($"GRPC shutdown complete.");
        }
    }
}