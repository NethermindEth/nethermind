// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Modules
{
    using Pool = (Func<bool, Task<IRpcModule>> RentModule, Action<IRpcModule> ReturnModule, IRpcModulePool ModulePool);

    public class RpcModuleProvider : IRpcModuleProvider
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _enabledModules = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, ResolvedMethodInfo> _methods = new();
        private Dictionary<string, Pool> _pools = new();
        private FrozenDictionary<string, ResolvedMethodInfo>? _frozenMethods = null;
        private FrozenDictionary<string, Pool>? _frozenPools = null;

        private readonly IRpcMethodFilter _filter = NullRpcMethodFilter.Instance;

        private readonly Lock _updateRegistrationsLock = new();
        private readonly MethodInfo _registerMethod;

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, IJsonSerializer serializer, ILogManager logManager)
            : this(fileSystem, jsonRpcConfig, serializer, [], logManager)
        {
        }

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, IJsonSerializer serializer, IReadOnlyList<RpcModuleInfo> rpcModules, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            if (fileSystem.File.Exists(_jsonRpcConfig.CallsFilterFilePath))
            {
                if (_logger.IsWarn) _logger.Warn("Applying JSON RPC filter.");
                _filter = new RpcMethodFilter(_jsonRpcConfig.CallsFilterFilePath, fileSystem, _logger);
            }

            _registerMethod = GetType().GetMethods().First(m => m.Name == nameof(Register));
            foreach (var rpcModuleInfo in rpcModules)
            {
                RegisterNonGeneric(rpcModuleInfo.ModuleType, rpcModuleInfo.Pool);
            }
        }

        public IJsonSerializer Serializer { get; }

        public IReadOnlyCollection<string> Enabled => _enabledModules;

        public IReadOnlyCollection<string> All => _modules;

        private void RegisterNonGeneric(Type moduleType, IRpcModulePool pool)
        {
            // Hey its either this of changing like, 5 class.
            MethodInfo generic = _registerMethod.MakeGenericMethod(moduleType);
            generic.Invoke(this, [pool]);
        }

        public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule
        {
            Type moduleClass = typeof(T);
            RpcModuleAttribute attribute = moduleClass.GetCustomAttribute<RpcModuleAttribute>();
            if (attribute is null)
            {
                if (_logger.IsWarn) _logger.Warn($"Cannot register {moduleClass.Name} as a JSON RPC module because it does not have a {nameof(RpcModuleAttribute)} applied.");
                return;
            }

            if (_logger.IsTrace) _logger.Trace($"Registering module {moduleClass.Name} as part of {attribute.ModuleType} module");
            string moduleType = attribute.ModuleType;
            lock (_updateRegistrationsLock)
            {
                var methods = GetMethods<T>(moduleType).ToArray();
                var poolRecord = GetPool(pool);

                methods
                    .ForEach((method) =>
                    {
                        _pools[method.Key] = poolRecord;
                        _methods[method.Key] = method.Value;
                    });
                _frozenPools = null;
                _frozenMethods = null;

                _modules.Add(moduleType);

                if (_jsonRpcConfig.EnabledModules.Contains(moduleType, StringComparer.OrdinalIgnoreCase))
                {
                    _enabledModules.Add(moduleType);
                }
            }
        }

        private Pool GetPool<T>(IRpcModulePool<T> pool) where T : IRpcModule
        {
            return (async canBeShared => await pool.GetModule(canBeShared), m => pool.ReturnModule((T)m), pool);
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

        private void EnsureFrozenCollection()
        {
            _frozenPools ??= _pools.ToFrozenDictionary(StringComparer.Ordinal);
            _frozenMethods ??= _methods.ToFrozenDictionary(StringComparer.Ordinal);
        }

        public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module)
        {
            EnsureFrozenCollection();
            module = null;

            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result))
            {
                return ModuleResolution.Unknown;
            }

            module = result.ModuleType;

            if ((result.Availability & context.RpcEndpoint) == RpcEndpoint.None)
            {
                return ModuleResolution.EndpointDisabled;
            }

            if (context.Url is not null)
            {
                return context.Url.EnabledModules.Contains(result.ModuleType, StringComparer.OrdinalIgnoreCase)
                    ? ModuleResolution.Enabled
                    : ModuleResolution.Disabled;
            }

            return _enabledModules.Contains(result.ModuleType) ? ModuleResolution.Enabled : ModuleResolution.Disabled;
        }

        public ResolvedMethodInfo? Resolve(string methodName)
        {
            EnsureFrozenCollection();
            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result)) return null;

            return result;
        }

        public Task<IRpcModule> Rent(string methodName, bool canBeShared)
        {
            EnsureFrozenCollection();
            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result)) return null;

            return _frozenPools[methodName].RentModule(canBeShared);
        }

        public void Return(string methodName, IRpcModule rpcModule)
        {
            EnsureFrozenCollection();
            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result))
                throw new InvalidOperationException("Not possible to return an unresolved module");

            _frozenPools[methodName].ReturnModule(rpcModule);
        }

        public IRpcModulePool? GetPoolForMethod(string methodName)
        {
            EnsureFrozenCollection();
            return _frozenPools.TryGetValue(methodName, out var poolInfo) ? poolInfo.ModulePool : null;
        }

        private static IDictionary<string, (MethodInfo, bool, RpcEndpoint)> GetMethodDict(Type type)
        {
            BindingFlags methodFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;

            IEnumerable<MethodInfo> methods = type.GetMethods(methodFlags)
                .Concat(type.GetInterfaces().SelectMany(i => i.GetMethods(methodFlags)))
                .DistinctBy(x => x.Name);

            return methods.ToDictionary(
                x => x.Name.Trim(),
                x =>
                {
                    JsonRpcMethodAttribute? jsonRpcMethodAttribute = x.GetCustomAttribute<JsonRpcMethodAttribute>();
                    return (x, jsonRpcMethodAttribute?.IsSharable ?? true, jsonRpcMethodAttribute?.Availability ?? RpcEndpoint.All);
                });
        }

        public class ResolvedMethodInfo
        {
            public readonly struct ExpectedParameter
            {
                public readonly ParameterInfo Info;
                public readonly ConstructorInvoker? ConstructorInvoker;
                private readonly ParameterDetails _introspection;

                public bool IsNullable => (_introspection & ParameterDetails.IsNullable) != 0;
                public bool IsIJsonRpcParam => ConstructorInvoker is not null;
                public bool IsOptional => (_introspection & ParameterDetails.IsOptional) != 0;

                public IJsonRpcParam CreateRpcParam()
                {
                    ConstructorInvoker? constructorInvoker = ConstructorInvoker;
                    if (constructorInvoker is null)
                    {
                        ThrowNotJsonRpc();
                    }

                    return Unsafe.As<IJsonRpcParam>(constructorInvoker.Invoke([]));

                    [DoesNotReturn]
                    [StackTraceHidden]
                    static void ThrowNotJsonRpc()
                    {
                        throw new InvalidOperationException("This parameter is not an IJsonRpcParam");
                    }
                }

                internal ExpectedParameter(ParameterInfo info, ConstructorInvoker? constructor, ParameterDetails introspection)
                {
                    ArgumentNullException.ThrowIfNull(info);

                    Info = info;
                    ConstructorInvoker = constructor;
                    _introspection = introspection;
                }
            }

            [Flags]
            public enum ParameterDetails
            {
                None,
                IsNullable = 0b1,
                IsOptional = 0b10,
            }

            public ResolvedMethodInfo()
            {
                ExpectedParameters = [];
            }

            public ResolvedMethodInfo(
                string moduleType,
                MethodInfo methodInfo,
                bool readOnly,
                RpcEndpoint availability)
            {
                ModuleType = moduleType;
                MethodInfo = methodInfo;

                ParameterInfo[] parameters = methodInfo.GetParameters();
                ExpectedParameter[] expectedParameters = new ExpectedParameter[parameters.Length];
                for (var i = 0; i < parameters.Length; i++)
                {
                    ParameterInfo parameter = parameters[i];
                    ConstructorInvoker? constructor = null;
                    ParameterDetails details = ParameterDetails.None;
                    if (parameter.ParameterType.IsAssignableTo(typeof(IJsonRpcParam)))
                    {
                        constructor = ConstructorInvoker.Create(parameter.ParameterType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, []));
                    }

                    if (IsNullableParameter(parameter))
                    {
                        details |= ParameterDetails.IsNullable;
                    }
                    if (parameter.IsOptional)
                    {
                        details |= ParameterDetails.IsOptional;
                    }

                    expectedParameters[i] = new(parameter, constructor, details);
                }

                ExpectedParameters = expectedParameters;
                ReadOnly = readOnly;
                Availability = availability;
                Invoker = MethodInvoker.Create(methodInfo);
            }

            public string ModuleType { get; }
            public MethodInfo MethodInfo { get; }
            public MethodInvoker Invoker { get; }
            public ExpectedParameter[] ExpectedParameters { get; }
            public bool ReadOnly { get; }
            public RpcEndpoint Availability { get; }

            public override string ToString()
            {
                return MethodInfo.Name;
            }

            private static bool IsNullableParameter(ParameterInfo parameterInfo)
            {
                Type parameterType = parameterInfo.ParameterType;
                if (parameterType.IsValueType)
                {
                    return Nullable.GetUnderlyingType(parameterType) is not null;
                }

                NullableAttribute? nullableAttribute = parameterInfo.GetCustomAttribute<NullableAttribute>();
                if (nullableAttribute is not null)
                {
                    byte[] flags = nullableAttribute.NullableFlags;
                    return flags.Length >= 1 && flags[0] == 2;
                }
                return false;
            }
        }
    }

    public record RpcModuleInfo(Type ModuleType, IRpcModulePool Pool);
}
