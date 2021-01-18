using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin: INethermindPlugin, INethermindServicesPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;
        private INodeHealthService _nodeHealthService;
        private ILogger _logger;

        public void Dispose()
        {
        }

        public string Name => "HealthChecks";

        public string Description => "Endpoints that takes care of node`s health";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _api = api;
            _healthChecksConfig = _api.Config<IHealthChecksConfig>();
            IInitConfig initConfig = _api.Config<IInitConfig>();
            _nodeHealthService = new NodeHealthService(_api.RpcModuleProvider, _api.BlockchainProcessor, _api.BlockProducer, _healthChecksConfig, _api.ChainSpec, initConfig.IsMining);
            _logger = api.LogManager.GetClassLogger();
            
            return Task.CompletedTask;
        }

        public void AddServices(IServiceCollection service)
        {
            service.AddHealthChecks()
                .AddTypeActivatedCheck<NodeHealthCheck>(
                    "node-health", 
                    args: new object[] { _nodeHealthService });
            if (_healthChecksConfig.UIEnabled)
            {
                service.AddHealthChecksUI(setup =>
                {
                    setup.AddHealthCheckEndpoint("health", _healthChecksConfig.Slug);
                    setup.SetEvaluationTimeInSeconds(_healthChecksConfig.PollingInterval);
                    setup.SetHeaderText("Nethermind Node Health");
                    if (_healthChecksConfig.WebhooksEnabled) 
                    {
                        setup.AddWebhookNotification("webhook", uri: _healthChecksConfig.WebhooksUri, payload: _healthChecksConfig.WebhooksPayload, restorePayload: _healthChecksConfig.WebhooksRestorePayload);
                    }
                })
                .AddInMemoryStorage();
            }
        }
        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_healthChecksConfig.Enabled)
            {
                HealthModule healthModule = new HealthModule(_nodeHealthService);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IHealthModule>(healthModule, true));
                if (_logger.IsInfo) _logger.Info("Health RPC Module has been enabled");
            }

            return Task.CompletedTask;
        }
    }
}
