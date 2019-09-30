using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cortex.BeaconNode
{
    // TODO: Move to worker / services library
    public class Worker : BackgroundService
    {
        private const string YamlConfigKey = "config";

        private readonly IConfiguration _configuration;
        private readonly BeaconNodeConfiguration _beaconNodeConfiguration;
        private readonly ILogger _logger;

        public Worker(ILogger<Worker> logger, IConfiguration configuration, BeaconNodeConfiguration beaconNodeConfiguration)
        {
            _logger = logger;
            _configuration = configuration;
            _beaconNodeConfiguration = beaconNodeConfiguration;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var version = _beaconNodeConfiguration.Version;
            var yamlConfig = _configuration[YamlConfigKey];
            _logger.LogInformation("'{ProductTokenVersion}' started with config '{Config}'",
                version, yamlConfig);

            while (!stoppingToken.IsCancellationRequested)
            {
                //_logger.LogInformation("Worker running at: {time}", DateTimeOffset.Now);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
