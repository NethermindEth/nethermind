using System.Threading.Tasks;
using Nethermind.Grpc;
using Nethermind.Grpc.Clients;
using Nethermind.Logging;

namespace Nethermind.Runner.Runners
{
    public class GrpcClientRunner : IRunner
    {
        private readonly IGrpcClient _service;
        private readonly IGrpcClientConfig _config;
        private readonly ILogger _logger;

        public GrpcClientRunner(IGrpcClient service, IGrpcClientConfig config, ILogManager logManager)
        {
            _service = service;
            _config = config;
            _logger = logManager.GetClassLogger();
        }

        public Task Start()
        {
            if (_logger.IsInfo) _logger.Info($"Connecting GRPC client to {_config.Host}:{_config.Port}.");
            return _service.StartAsync();
        }

        public Task StopAsync()
        {
            if (_logger.IsInfo) _logger.Info($"Stopping GRPC client...");
            return _service.StopAsync();
        }
    }
}