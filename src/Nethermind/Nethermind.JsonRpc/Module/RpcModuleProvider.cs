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

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.Config;

namespace Nethermind.JsonRpc.Module
{
    public class RpcModuleProvider : IRpcModuleProvider
    {
        private readonly IJsonRpcConfig _jsonRpcConfig;
        private List<ModuleInfo> _modules = new List<ModuleInfo>();
        private List<ModuleInfo> _enabledModules = new List<ModuleInfo>();

        public RpcModuleProvider(IJsonRpcConfig jsonRpcConfig)
        {
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
        }

        public void Register<T>(IModule module) where T : IModule
        {
            ModuleInfo moduleInfo = new ModuleInfo(module.ModuleType, typeof(T), module);
            _modules.Add(moduleInfo);
            if (_jsonRpcConfig.EnabledModules.Contains(module.ModuleType))
            {
                _enabledModules.Add(moduleInfo);
            }
        }

        public IReadOnlyCollection<ModuleInfo> GetEnabledModules()
        {
            return _enabledModules;
        }

        public IReadOnlyCollection<ModuleInfo> GetAllModules()
        {
            return _modules;
        }
    }
}