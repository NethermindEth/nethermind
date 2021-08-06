//  Copyright (c) 2021 Demerzel Solutions Limited
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
using System.Threading.Tasks;
using Nethermind.Logging;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules
{
    public class RpcModuleProvider : IRpcModuleProvider
    {
        private ILogger _logger;
        private IJsonRpcConfig _jsonRpcConfig;
        
        private List<string> _modules = new();
        private List<string> _enabledModules = new();
        
        private Dictionary<string, ResolvedMethodInfo> _methods
            = new(StringComparer.InvariantCulture);
        
        private readonly Dictionary<string, (Func<bool, Task<IRpcModule>> RentModule, Action<IRpcModule> ReturnModule)> _pools
            = new();
        
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

        public IReadOnlyCollection<string> Enabled => _enabledModules;

        public IReadOnlyCollection<string> All => _modules;

        public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule
        {
            RpcModuleAttribute attribute = typeof(T).GetCustomAttribute<RpcModuleAttribute>();
            if (attribute == null)
            {
                if(_logger.IsWarn) _logger.Warn(
                    $"Cannot register {typeof(T).Name} as a JSON RPC module because it does not have a {nameof(RpcModuleAttribute)} applied.");
                return;
            }
            
            string moduleType = attribute.ModuleType;

            _pools[moduleType] = (async canBeShared => await pool.GetModule(canBeShared), m => pool.ReturnModule((T) m));
            _modules.Add(moduleType);

            ((List<JsonConverter>) Converters).AddRange(pool.Factory.GetConverters());

            foreach ((string name, (MethodInfo info, bool readOnly, RpcEndpoint availability)) in GetMethodDict(typeof(T)))
            {
                ResolvedMethodInfo resolvedMethodInfo = new(moduleType, info, readOnly, availability);
                if (_filter.AcceptMethod(resolvedMethodInfo.ToString()))
                {
                    _methods[name] = resolvedMethodInfo;
                }
            }

            if (_jsonRpcConfig.EnabledModules.Contains(moduleType, StringComparer.InvariantCultureIgnoreCase))
            {
                _enabledModules.Add(moduleType);
            }
        }

        public ModuleResolution Check(string methodName, RpcEndpoint rpcEndpoint)
        {
            if (!_methods.TryGetValue(methodName, out ResolvedMethodInfo result)) return ModuleResolution.Unknown;

            if ((result.Availability & rpcEndpoint) == RpcEndpoint.None) return ModuleResolution.EndpointDisabled;
            
            return _enabledModules.Contains(result.ModuleType) ? ModuleResolution.Enabled : ModuleResolution.Disabled;
        }

        public (MethodInfo, bool) Resolve(string methodName)
        {
            if (!_methods.TryGetValue(methodName, out ResolvedMethodInfo result)) return (null, false);

            return (result.MethodInfo, result.ReadOnly);
        }

        public Task<IRpcModule> Rent(string methodName, bool canBeShared)
        {
            if (!_methods.TryGetValue(methodName, out ResolvedMethodInfo result)) return null;

            return _pools[result.ModuleType].RentModule(canBeShared);
        }

        public void Return(string methodName, IRpcModule rpcModule)
        {
            if (!_methods.TryGetValue(methodName, out ResolvedMethodInfo result))
                throw new InvalidOperationException("Not possible to return an unresolved module");

            _pools[result.ModuleType].ReturnModule(rpcModule);
        }

        private IDictionary<string, (MethodInfo, bool, RpcEndpoint)> GetMethodDict(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return methods.ToDictionary(
                x => x.Name.Trim(),
                x =>
                {
                    JsonRpcMethodAttribute? jsonRpcMethodAttribute = x.GetCustomAttribute<JsonRpcMethodAttribute>();
                    return (x, jsonRpcMethodAttribute?.IsSharable ?? true, jsonRpcMethodAttribute?.Availability ?? RpcEndpoint.All);
                });
        }
        
        private class ResolvedMethodInfo
        {
            public ResolvedMethodInfo(
                string moduleType,
                MethodInfo methodInfo,
                bool readOnly,
                RpcEndpoint availability)
            {
                ModuleType = moduleType;
                MethodInfo = methodInfo;
                ReadOnly = readOnly;
                Availability = availability;
            }
            
            public string ModuleType { get; }
            public MethodInfo MethodInfo { get; }
            public bool ReadOnly { get; }
            public RpcEndpoint Availability { get; }

            public override string ToString()
            {
                return MethodInfo.Name;
            }
        }
    }
}
