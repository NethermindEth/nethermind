/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
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