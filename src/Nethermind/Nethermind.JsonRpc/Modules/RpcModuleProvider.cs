// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        private readonly HashSet<string> _modules = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly HashSet<string> _enabledModules = new(StringComparer.InvariantCultureIgnoreCase);
        private readonly Dictionary<string, ResolvedMethodInfo> _methods = new(StringComparer.InvariantCulture);

        private readonly Dictionary<string, (Func<bool, Task<IRpcModule>> RentModule, Action<IRpcModule> ReturnModule, IRpcModulePool ModulePool)> _pools = new();

        private readonly IRpcMethodFilter _filter = NullRpcMethodFilter.Instance;

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            if (fileSystem.File.Exists(_jsonRpcConfig.CallsFilterFilePath))
            {
                if (_logger.IsWarn) _logger.Warn("Applying JSON RPC filter.");
                _filter = new RpcMethodFilter(_jsonRpcConfig.CallsFilterFilePath, fileSystem, _logger);
            }
        }

        public JsonSerializer Serializer { get; } = new();

        public IReadOnlyCollection<JsonConverter> Converters { get; } = new List<JsonConverter>();

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

            _pools[moduleType] = (async canBeShared => await pool.GetModule(canBeShared), m => pool.ReturnModule((T)m), pool);
            _modules.Add(moduleType);

            IReadOnlyCollection<JsonConverter> poolConverters = pool.Factory.GetConverters();
            ((List<JsonConverter>)Converters).AddRange(poolConverters);

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

        public IRpcModulePool? GetPool(string moduleType) => _pools.TryGetValue(moduleType, out var poolInfo) ? poolInfo.ModulePool : null;

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
