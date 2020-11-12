using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Baseline;
using Nethermind.Baseline.Config;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;

namespace Nethermind.Plugin.Baseline
{
    public class BaselinePlugin : INethermindPlugin
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
                    _api.LogManager,
                    _api.MainBlockProcessor,
                    _api.DisposeStack);

                var modulePool = new SingletonModulePool<IBaselineModule>(baselineModuleFactory);
                _api.RpcModuleProvider!.Register(modulePool);
                
                if (_logger.IsInfo) _logger.Info("Baseline RPC Module has been enabled");
            }
            else
            {
                if (_logger.IsWarn) _logger.Info("Skipping Baseline RPC due to baseline being disabled in config.");
            }

            return Task.CompletedTask;
        }
    }
}
