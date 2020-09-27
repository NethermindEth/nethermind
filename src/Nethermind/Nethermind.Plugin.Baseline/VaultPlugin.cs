using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Baseline.Config;
using Nethermind.Baseline.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Vault;
using Nethermind.Vault.Config;
using Nethermind.Vault.JsonRpc;

namespace Nethermind.Plugin.Baseline
{
    public class VaultPlugin : INethermindPlugin
    {
        private INethermindApi _api;

        private ILogger _logger;

        private IVaultConfig _vaultConfig;

        public void Dispose() { }

        public string Name => "Vault";

        public string Description => "Provide Vault Connector";

        public string Author => "Nethermind";

        public Task Init(INethermindApi api)
        {
            _vaultConfig = api.Config<IVaultConfig>();
            _api = api;
            _logger = api.LogManager.GetClassLogger();
            return Task.CompletedTask;
        }

        public Task InitNetworkProtocol()
        {
            return Task.CompletedTask;
        }

        public Task InitRpcModules()
        {
            if (_vaultConfig.Enabled)
            {
                VaultService vaultService = new VaultService(_vaultConfig, _api.LogManager);
                VaultModule vaultModule = new VaultModule(vaultService, _api.LogManager);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IVaultModule>(vaultModule, true));
                if (_logger.IsInfo) _logger.Info("Vault RPC Module has been enabled");
            }
            
            return Task.CompletedTask;
        }
    }
}