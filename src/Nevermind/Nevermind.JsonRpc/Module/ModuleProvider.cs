using System.Collections.Generic;
using System.Linq;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class ModuleProvider : IModuleProvider
    {
        private readonly IConfigurationProvider _configurationProvider;
        private ModuleInfo[] _modules;
        private ModuleInfo[] _enabledModules;

        public ModuleProvider(IConfigurationProvider configurationProvider, INetModule netModule, IEthModule ethModule, IWeb3Module web3Module, IShhModule shhModule)
        {
            _configurationProvider = configurationProvider;
            Initialize(netModule, ethModule, web3Module, shhModule);
        }

        public IReadOnlyCollection<ModuleInfo> GetEnabledModules()
        {
            return _enabledModules;
        }

        public IReadOnlyCollection<ModuleInfo> GetAllModules()
        {
            return _modules;
        }

        private void Initialize(INetModule netModule, IEthModule ethModule, IWeb3Module web3Module, IShhModule shhModule)
        {
            _modules = new[]
            {
                new ModuleInfo(ModuleType.Net, typeof(INetModule), netModule),
                new ModuleInfo(ModuleType.Eth, typeof(IEthModule), ethModule),
                new ModuleInfo(ModuleType.Web3, typeof(IWeb3Module), web3Module),
                new ModuleInfo(ModuleType.Shh, typeof(IShhModule), shhModule)
            };

            _enabledModules = _modules.Where(x => _configurationProvider.EnabledModules.Contains(x.ModuleType)).ToArray();
        }
    }
}