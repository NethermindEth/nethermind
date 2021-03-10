using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore;
using Nethermind.Logging;
using Nethermind.TxPool;
using Nethermind.Vault;
using Nethermind.Vault.Config;
using Nethermind.Vault.JsonRpc;
using Nethermind.Vault.KeyStore;

namespace Nethermind.Plugin.Baseline
{
    public class VaultPlugin : INethermindPlugin
    {
        private INethermindApi _api;

        private ILogger _logger;

        private IVaultConfig _vaultConfig;
        private VaultService _vaultService;
        private IVaultSealingHelper _vaultSealingHelper;

        public void Dispose()
        {
            if (_vaultConfig != null && _vaultConfig.Enabled)
            {
                _vaultSealingHelper?.Seal();
            }
        }

        public string Name => "Vault";

        public string Description => "Provide Vault Connector";

        public string Author => "Nethermind";

        public async Task Init(INethermindApi api)
        {
            _vaultConfig = api.Config<IVaultConfig>();
            _api = api;
            _logger = api.LogManager.GetClassLogger();
            if (_vaultConfig.Enabled)
            {
                _vaultService = new VaultService(_vaultConfig, _api.LogManager);

                var passwordProvider = new FilePasswordProvider(a => _vaultConfig.VaultKeyFile.GetApplicationResourcePath())
                                            .OrReadFromConsole("Provide passsphrase to unlock Vault");
                var vaultKeyStoreFacade = new VaultKeyStoreFacade(passwordProvider);
                _vaultSealingHelper = new VaultSealingHelper(vaultKeyStoreFacade, _vaultConfig, _logger);
                await _vaultSealingHelper.Unseal();


                IVaultWallet wallet = new VaultWallet(_vaultService, _vaultConfig.VaultId, _api.LogManager);
                ITxSigner vaultSigner = new VaultTxSigner(wallet, _api.ChainSpec.ChainId);

                // TODO: change vault to provide, use sealer to set the gas price as well
                // TODO: need to verify the timing of initializations so the TxSender replacement works fine
                _api.TxSender = new VaultTxSender(vaultSigner, _vaultConfig, (int)_api.ChainSpec.ChainId);
            }
        }

        public Task InitBlockchain()
        {
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
                VaultModule vaultModule = new VaultModule(_vaultService, _api.LogManager);
                _api.RpcModuleProvider!.Register(new SingletonModulePool<IVaultModule>(vaultModule, true));
                if (_logger.IsInfo) _logger.Info("Vault RPC Module has been enabled");
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }
}
