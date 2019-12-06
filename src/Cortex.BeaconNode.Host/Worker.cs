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

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Worker stopping.");
            _stopped = true;
            await base.StopAsync(cancellationToken);
        }

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogDebug("Worker starting.");
            await base.StartAsync(cancellationToken);
            _logger.LogDebug("Worker started.");
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var version = _beaconNodeConfiguration.Version;
            var yamlConfig = _configuration[ConfigKey];
            _logger.LogInformation(HostEvent.WorkerStarted, "{ProductTokenVersion} started; {Environment} environment (config '{Config}') [{ThreadId}]",
                version, _environment.EnvironmentName, yamlConfig, Thread.CurrentThread.ManagedThreadId);

            //await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

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
                        _logger.LogInformation(0, "Store found with genesis time {GenesisTime}, starting clock tick [{ThreadId}]",
                            store.GenesisTime, Thread.CurrentThread.ManagedThreadId);
                    }
                }
                if (store != null)
                {
                    _forkChoice.OnTick(store, (ulong)time.ToUnixTimeSeconds());
                }
                // Wait for remaining time, if any
                // NOTE: To fast forward time during testing, have the second call to test _clock.Now() jump forward to avoid waiting.
                var remaining = time.AddSeconds(1) - _clock.Now();
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, stoppingToken);
                }
                //await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }

            _logger.LogDebug("Worker execute thread exiting [{ThreadId}].", Thread.CurrentThread.ManagedThreadId);
        }
    }
}
