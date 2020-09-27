using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Baseline.Config;
using Nethermind.Baseline.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Plugin.Baseline
{
    public class BaselinePlugin : IPlugin
    {
        private INethermindApi _api;
        
        private ILogger _logger;
        
        private IBaselineConfig _baselineConfig;

        public void Dispose() { }

        public string Name => "Baseline";
        
        public string Description => "Ethereum Baseline for Enterprise";
        
        public string Author => "Nethermind";
        
        public Task Init(INethermindApi api)
        {
            _baselineConfig = api.Config<IBaselineConfig>();
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
            if (_baselineConfig.Enabled)
            {
                BaselineModuleFactory baselineModuleFactory = new BaselineModuleFactory(
                    _api.TxSender!,
                    _api.StateReader!,
                    _api.CreateBlockchainBridge(),
                    _api.BlockTree!,
                    _api.AbiEncoder,
                    _api.FileSystem,
                    _api.LogManager);

                var modulePool = new BoundedModulePool<IBaselineModule>(baselineModuleFactory, 2);
                _api.RpcModuleProvider!.Register(modulePool);
                
                // TODO: we can probably do a default config for all plugins as INameConfig and Enabled property there
                if (_logger.IsInfo) _logger.Info("Baseline RPC Module has been enabled");
            }

            return Task.CompletedTask;
        }
    }
}