using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Nethermind.BeaconNode.Services;
using Nethermind.BeaconNode.Storage;

namespace Nethermind.BeaconNode
{
    public class BeaconNodeWorker : BackgroundService
    {
        private const string ConfigKey = "config";

        private readonly IConfiguration _configuration;
        private readonly BeaconNodeConfiguration _beaconNodeConfiguration;
        private readonly IStoreProvider _storeProvider;
        private readonly ForkChoice _forkChoice;
        private readonly INodeStart _nodeStart;
        private readonly ILogger _logger;
        private readonly IClock _clock;
        private readonly IHostEnvironment _environment;
        private bool _stopped;

        public BeaconNodeWorker(ILogger<BeaconNodeWorker> logger,
            IClock clock,
            IHostEnvironment environment,
            IConfiguration configuration,
            BeaconNodeConfiguration beaconNodeConfiguration,
            IStoreProvider storeProvider,
            ForkChoice forkChoice,
            INodeStart nodeStart)
        {
            _logger = logger;
            _clock = clock;
            _environment = environment;
            _configuration = configuration;
            _beaconNodeConfiguration = beaconNodeConfiguration;
            _storeProvider = storeProvider;
            _forkChoice = forkChoice;
            _nodeStart = nodeStart;
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
            var environmentName = _environment.EnvironmentName;
            var configName = _configuration[ConfigKey];
            _logger.LogInformation(Event.WorkerStarted, "{ProductTokenVersion} started; {Environment} environment (config '{Config}') [{ThreadId}]",
                version, environmentName, configName, Thread.CurrentThread.ManagedThreadId);

            await _nodeStart.InitializeNodeAsync();

            IStore? store = null;
            while (!stoppingToken.IsCancellationRequested && !_stopped)
            {
                var time = _clock.Now();
                if (store == null)
                {
                    store = _storeProvider.GetStore();
                    if (store != null)
                    {
                        _logger.LogInformation(Event.WorkerStoreAvailableTickStarted, "Store available with genesis time {GenesisTime}, starting clock tick [{ThreadId}]",
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
            }

            _logger.LogDebug("Worker execute thread exiting [{ThreadId}].", Thread.CurrentThread.ManagedThreadId);
        }
    }
}
