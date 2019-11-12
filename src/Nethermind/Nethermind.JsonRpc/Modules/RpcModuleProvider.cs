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
using System.Reflection;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules
{
    public class RpcModuleProvider : IRpcModuleProvider
    {
        public IReadOnlyCollection<JsonConverter> Converters { get; } = new List<JsonConverter>();
        
        private ILogger _logger;
        private IJsonRpcConfig _jsonRpcConfig;
        private Dictionary<string, (ModuleType ModuleType,(MethodInfo MethodInfo, bool ReadOnly) Method)> _methods = new Dictionary<string, (ModuleType ModuleType, (MethodInfo MethodInfo, bool ReadOnly) Method)>(StringComparer.InvariantCultureIgnoreCase);
        private Dictionary<ModuleType, (Func<bool, IModule> RentModule, Action<IModule> ReturnModule)> _pools = new Dictionary<ModuleType, (Func<bool, IModule> RentModule, Action<IModule> ReturnModule)>();

        private List<ModuleType> _modules = new List<ModuleType>();
        private List<ModuleType> _enabledModules = new List<ModuleType>();

        public RpcModuleProvider(IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        private IDictionary<string, (MethodInfo, bool)> GetMethodDict(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            

            return methods.ToDictionary(x => x.Name.Trim().ToLower(), x => (x, x.GetCustomAttribute<JsonRpcMethodAttribute>()?.IsReadOnly ?? true));
        }

        public void Register<T>(IRpcModulePool<T> pool) where T : IModule
        {
            ModuleType moduleType = typeof(T).GetCustomAttribute<RpcModuleAttribute>().ModuleType;

            _pools[moduleType] = (canBeShared => pool.GetModule(canBeShared), (m) => pool.ReturnModule((T)m));
            _modules.Add(moduleType);
            
            ((List<JsonConverter>)Converters).AddRange(pool.Factory.GetConverters());

            foreach ((string name, (MethodInfo Info, bool ReadOnly) method) in GetMethodDict(typeof(T)))
            {
                _methods[name] = (moduleType, method);
            }

            if (_jsonRpcConfig.EnabledModules.Contains(moduleType.ToString(), StringComparer.InvariantCultureIgnoreCase))
            {
                _enabledModules.Add(moduleType);
            }
        }

        public IReadOnlyCollection<ModuleType> Enabled => _enabledModules;

        public IReadOnlyCollection<ModuleType> All => _modules;
        
        public ModuleResolution Check(string methodName)
        {
            if (!_methods.ContainsKey(methodName))
            {
                return ModuleResolution.Unknown;
            }
            
            var result = _methods[methodName];
            return _enabledModules.Contains(result.ModuleType) ? ModuleResolution.Enabled : ModuleResolution.Disabled;
        }

        public (MethodInfo, bool) Resolve(string methodName)
        {
            if (!_methods.ContainsKey(methodName))
            {
                return (null, false);
            }
            
            var result = _methods[methodName];
            return result.Method;
        }
        
        public IModule Rent(string methodName, bool canBeShared)
        {
            if (!_methods.ContainsKey(methodName))
            {
                return null;
            }
            
            var result = _methods[methodName];
            return _pools[result.ModuleType].RentModule(canBeShared);
        }
        
        public void Return(string methodName, IModule module)
        {
            if (!_methods.ContainsKey(methodName))
            {
                throw new InvalidOperationException("Not possible to return an unresolved module");
            }
            
            var result = _methods[methodName];
            _pools[result.ModuleType].ReturnModule(module);
        }
    }
}