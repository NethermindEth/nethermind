using System;
using System.Threading;
using System.Threading.Tasks;
using Cortex.BeaconNode.MockedStart;
using Cortex.BeaconNode.Services;
using Cortex.BeaconNode.Storage;
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
        private readonly IStoreProvider _storeProvider;
        private readonly ForkChoice _forkChoice;
        private readonly QuickStart? _quickStart;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly IHostEnvironment _environment;
        private bool _stopped;

        public Worker(ILogger<Worker> logger,
            IClock clock,
            IHostEnvironment environment,
            IConfiguration configuration,
            BeaconNodeConfiguration beaconNodeConfiguration,
            IStoreProvider storeProvider,
            ForkChoice forkChoice,
            QuickStart? quickStart)
        {
            _logger = logger;
            _clock = clock;
            _environment = environment;
            _configuration = configuration;
            _beaconNodeConfiguration = beaconNodeConfiguration;
            _storeProvider = storeProvider;
            _forkChoice = forkChoice;
            // Replace QuickStart with IChainStartup, which can be replaced by real/mocked
            _quickStart = quickStart;
        }

        public override Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Worker stopping.");
            _stopped = true;
            return base.StopAsync(cancellationToken);
        }

        public override Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Worker starting.");
            return base.StartAsync(cancellationToken);
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

            IStore? store = null;
            while (!stoppingToken.IsCancellationRequested && !_stopped)
            {
                var time = _clock.Now();
                if (store == null)
                {
                    store = _storeProvider.GetStore();
                    if (store != null)
                    {
                        _logger.LogInformation(0, "Store found with genesis time {GenesisTime}, starting clock tick",
                            store.GenesisTime);
                    }
                }
                if (store != null)
                {
                    _forkChoice.OnTick(store, (ulong)time.ToUnixTimeSeconds());
                }
                // Wait for remaining time, if any
                var remaining = _clock.Now() - (time.AddSeconds(1));
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, stoppingToken);
                }
            }
        }
    }
}
