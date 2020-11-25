using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Logging;

namespace Nethermind.HealthChecks
{
    public class HealthChecksPlugin: INethermindPlugin, INethermindServicesPlugin
    {
        private INethermindApi _api;
        private IHealthChecksConfig _healthChecksConfig;
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
            _logger =  _api.LogManager.GetClassLogger();

            if (_logger.IsWarn) _logger.Warn($"Health Checks Endpoint is enabled");
            return Task.CompletedTask;
        }

        public void AddServices(IServiceCollection service)
        {
            service.AddHealthChecks()
                .AddTypeActivatedCheck<NodeHealthCheck>(
                    "node-health", 
                    args: new object[] { _api.RpcModuleProvider });
            service.AddHealthChecksUI(setup =>
                {
                    setup.AddHealthCheckEndpoint("health", "/health");
                    setup.SetEvaluationTimeInSeconds(5);
                    setup.SetHeaderText("Nethermind Node Health");
                })
                .AddInMemoryStorage();
        }
        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            return Task.CompletedTask;
        }
    }
}
