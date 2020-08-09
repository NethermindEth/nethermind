//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules
{
    public class RpcModuleProvider : IRpcModuleProvider
    {
        private ILogger _logger;
        private IJsonRpcConfig _jsonRpcConfig;
        
        private List<ModuleType> _modules = new List<ModuleType>();
        private List<ModuleType> _enabledModules = new List<ModuleType>();
        
        private Dictionary<string, ResolvedMethodInfo> _methods
            = new Dictionary<string, ResolvedMethodInfo>(StringComparer.InvariantCultureIgnoreCase);
        
        private Dictionary<ModuleType, (Func<bool, IModule> RentModule, Action<IModule> ReturnModule)> _pools
            = new Dictionary<ModuleType, (Func<bool, IModule> RentModule, Action<IModule> ReturnModule)>();
        
        private IRpcMethodFilter _filter = NullRpcMethodFilter.Instance;

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            if (fileSystem.File.Exists(_jsonRpcConfig.CallsFilterFilePath))
            {
                if(_logger.IsWarn) _logger.Warn("Applying JSON RPC filter.");
                _filter = new RpcMethodFilter(_jsonRpcConfig.CallsFilterFilePath, fileSystem, _logger);
            }
        }

        public IReadOnlyCollection<JsonConverter> Converters { get; } = new List<JsonConverter>();

        public IReadOnlyCollection<ModuleType> Enabled => _enabledModules;

        public IReadOnlyCollection<ModuleType> All => _modules;

        public void Register<T>(IRpcModulePool<T> pool) where T : IModule
        {
            RpcModuleAttribute attribute = typeof(T).GetCustomAttribute<RpcModuleAttribute>();
            if (attribute == null)
            {
                if(_logger.IsWarn) _logger.Warn(
                    $"Cannot register {typeof(T).Name} as a JSON RPC module because it does not have a {nameof(RpcModuleAttribute)} applied.");
                return;
            }
            
            ModuleType moduleType = attribute.ModuleType;

            _pools[moduleType] = (canBeShared => pool.GetModule(canBeShared), m => pool.ReturnModule((T) m));
            _modules.Add(moduleType);

            ((List<JsonConverter>) Converters).AddRange(pool.Factory.GetConverters());

            foreach ((string name, (MethodInfo info, bool readOnly)) in GetMethodDict(typeof(T)))
            {
                ResolvedMethodInfo resolvedMethodInfo = new ResolvedMethodInfo(moduleType, info, readOnly);
                if (_filter.AcceptMethod(resolvedMethodInfo.ToString()))
                {
                    _methods[name] = resolvedMethodInfo;
                }
            }

            if (_jsonRpcConfig.EnabledModules.Contains(moduleType.ToString(), StringComparer.InvariantCultureIgnoreCase))
            {
                _enabledModules.Add(moduleType);
            }
        }

        public ModuleResolution Check(string methodName)
        {
            if (!_methods.ContainsKey(methodName)) return ModuleResolution.Unknown;

            ResolvedMethodInfo result = _methods[methodName];
            return _enabledModules.Contains(result.ModuleType) ? ModuleResolution.Enabled : ModuleResolution.Disabled;
        }

        public (MethodInfo, bool) Resolve(string methodName)
        {
            if (!_methods.ContainsKey(methodName)) return (null, false);

            ResolvedMethodInfo result = _methods[methodName];
            return (result.MethodInfo, result.ReadOnly);
        }

        public IModule Rent(string methodName, bool canBeShared)
        {
            if (!_methods.ContainsKey(methodName)) return null;

            ResolvedMethodInfo result = _methods[methodName];
            return _pools[result.ModuleType].RentModule(canBeShared);
        }

        public void Return(string methodName, IModule module)
        {
            if (!_methods.ContainsKey(methodName))
                throw new InvalidOperationException("Not possible to return an unresolved module");

            ResolvedMethodInfo result = _methods[methodName];
            _pools[result.ModuleType].ReturnModule(module);
        }

        private IDictionary<string, (MethodInfo, bool)> GetMethodDict(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return methods.ToDictionary(
                x => x.Name.Trim().ToLower(),
                x => (x, x.GetCustomAttribute<JsonRpcMethodAttribute>()?.IsReadOnly ?? true));
        }
        
        private class ResolvedMethodInfo
        {
            public ResolvedMethodInfo(
                ModuleType moduleType,
                MethodInfo methodInfo,
                bool readOnly)
            {
                ModuleType = moduleType;
                MethodInfo = methodInfo;
                ReadOnly = readOnly;
            }
            
            public ModuleType ModuleType { get; }
            public MethodInfo MethodInfo { get; }
            public bool ReadOnly { get; }

            public override string ToString()
            {
                return MethodInfo.Name.ToLowerInvariant();
            }
        }
    }
}