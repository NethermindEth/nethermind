using System.Threading;
using System.Threading.Tasks;
using Cortex.BeaconNode.MockedStart;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    // TODO: Move to worker / services library
    public class Worker : BackgroundService
    {
        private const string ConfigKey = "config";

        private readonly IConfiguration _configuration;
        private readonly BeaconNodeConfiguration _beaconNodeConfiguration;
        private readonly QuickStart? _quickStart;
        private readonly ILogger _logger;
        private readonly IHostEnvironment _environment;

        public Worker(ILogger<Worker> logger, 
            IHostEnvironment environment, 
            IConfiguration configuration, 
            BeaconNodeConfiguration beaconNodeConfiguration,
            QuickStart? quickStart)
        {
            _logger = logger;
            _environment = environment;
            _configuration = configuration;
            _beaconNodeConfiguration = beaconNodeConfiguration;
            // Replace QuickStart with IChainStartup, which can be replaced by real/mocked
            _quickStart = quickStart;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var version = _beaconNodeConfiguration.Version;
            var yamlConfig = _configuration[ConfigKey];
            _logger.LogInformation(HostEvent.WorkerStarted, "{ProductTokenVersion} started; {Environment} environment (config '{Config}')",
                version, _environment.EnvironmentName, yamlConfig);

            if (_quickStart != null)
            {
                _quickStart.QuickStartGenesis();
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                //_logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
