// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules
{
    using Pool = (Func<bool, Task<IRpcModule>> RentModule, Action<IRpcModule> ReturnModule, IRpcModulePool ModulePool);

    public class RpcModuleProvider : IRpcModuleProvider
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        private readonly HashSet<string> _modules = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly HashSet<string> _enabledModules = new(StringComparer.InvariantCultureIgnoreCase);

        private FrozenDictionary<string, ResolvedMethodInfo> _methods = FrozenDictionary<string, ResolvedMethodInfo>.Empty;
        private FrozenDictionary<string, Pool> _pools = FrozenDictionary<string, Pool>.Empty;

        private readonly IRpcMethodFilter _filter = NullRpcMethodFilter.Instance;

        private readonly object _updateRegistrationsLock = new();

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            Serializer = new EthereumJsonSerializer();
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            if (fileSystem.File.Exists(_jsonRpcConfig.CallsFilterFilePath))
            {
                if (_logger.IsWarn) _logger.Warn("Applying JSON RPC filter.");
                _filter = new RpcMethodFilter(_jsonRpcConfig.CallsFilterFilePath, fileSystem, _logger);
            }
        }

        public IJsonSerializer Serializer { get; }

        public IReadOnlyCollection<string> Enabled => _enabledModules;

        public IReadOnlyCollection<string> All => _modules;

        public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule
        {
            RpcModuleAttribute attribute = typeof(T).GetCustomAttribute<RpcModuleAttribute>();
            if (attribute is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot register {typeof(T).Name} as a JSON RPC module because it does not have a {nameof(RpcModuleAttribute)} applied.");
                return;
            }

            string moduleType = attribute.ModuleType;
            lock (_updateRegistrationsLock)
            {
                // FrozenDictionary can't be directly updated (which makes it fast for reading) so we combine the two sets of
                // data as an Enumerable create an new FrozenDictionary from it and then update the reference
                _pools = GetPools<T>(moduleType, pool).ToFrozenDictionary(StringComparer.Ordinal);
                _methods = _methods.Concat(GetMethods<T>(moduleType)).ToFrozenDictionary(StringComparer.Ordinal);

                _modules.Add(moduleType);

                if (_jsonRpcConfig.EnabledModules.Contains(moduleType, StringComparer.InvariantCultureIgnoreCase))
                {
                    _enabledModules.Add(moduleType);
                }
            }
        }

        private IEnumerable<KeyValuePair<string, Pool>> GetPools<T>(string moduleType, IRpcModulePool<T> pool) where T : IRpcModule
        {
            foreach (KeyValuePair<string, Pool> item in _pools)
            {
                yield return item;
            }

            yield return new(moduleType, (async canBeShared => await pool.GetModule(canBeShared), m => pool.ReturnModule((T)m), pool));
        }

        private IEnumerable<KeyValuePair<string, ResolvedMethodInfo>> GetMethods<T>(string moduleType) where T : IRpcModule
        {
            foreach ((string name, (MethodInfo info, bool readOnly, RpcEndpoint availability)) in GetMethodDict(typeof(T)))
            {
                ResolvedMethodInfo resolvedMethodInfo = new(moduleType, info, readOnly, availability);
                if (_filter.AcceptMethod(resolvedMethodInfo.ToString()))
                {
                    yield return new(name, resolvedMethodInfo);
                }
            }
        }

        public ModuleResolution Check(string methodName, JsonRpcContext context)
        {
            if (!_methods.TryGetValue(methodName, out ResolvedMethodInfo result))
                return ModuleResolution.Unknown;

            if ((result.Availability & context.RpcEndpoint) == RpcEndpoint.None)
                return ModuleResolution.EndpointDisabled;

            if (context.Url is not null)
                return context.Url.EnabledModules.Contains(result.ModuleType, StringComparer.InvariantCultureIgnoreCase) ? ModuleResolution.Enabled : ModuleResolution.Disabled;

            return _enabledModules.Contains(result.ModuleType) ? ModuleResolution.Enabled : ModuleResolution.Disabled;
        }

        public (MethodInfo, ParameterInfo[], bool) Resolve(string methodName)
        {
            if (!_methods.TryGetValue(methodName, out ResolvedMethodInfo result)) return (null, Array.Empty<ParameterInfo>(), false);

            return (result.MethodInfo, result.ExpectedParameters, result.ReadOnly);
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

        public IRpcModulePool? GetPool(string moduleType) => _pools.TryGetValue(moduleType, out var poolInfo) ? poolInfo.ModulePool : null;

        private static IDictionary<string, (MethodInfo, bool, RpcEndpoint)> GetMethodDict(Type type)
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
                ExpectedParameters = methodInfo.GetParameters();
                ReadOnly = readOnly;
                Availability = availability;
            }

            public string ModuleType { get; }
            public MethodInfo MethodInfo { get; }
            public ParameterInfo[] ExpectedParameters { get; }
            public bool ReadOnly { get; }
            public RpcEndpoint Availability { get; }

            public override string ToString()
            {
                return MethodInfo.Name;
            }
        }
    }
}
