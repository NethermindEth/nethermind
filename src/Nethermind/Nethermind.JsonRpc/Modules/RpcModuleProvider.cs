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
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.JsonRpc.Modules
{
    public class RpcModuleProvider : IRpcModuleProvider
    {
        private readonly ILogger _logger;
        private readonly IJsonRpcConfig _jsonRpcConfig;

        private readonly HashSet<string> _modules = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _enabledModules = new(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, ResolvedMethodInfo> _methods = new();
        private FrozenDictionary<string, ResolvedMethodInfo>? _frozenMethods = null;

        private readonly IRpcMethodFilter _filter = NullRpcMethodFilter.Instance;

        private readonly Lock _updateRegistrationsLock = new();
        private readonly MethodInfo _registerMethod;

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, IJsonSerializer serializer, ILogManager logManager)
            : this(fileSystem, jsonRpcConfig, serializer, [], logManager)
        {
        }

        public RpcModuleProvider(IFileSystem fileSystem, IJsonRpcConfig jsonRpcConfig, IJsonSerializer serializer, IReadOnlyList<RpcModuleInfo> rpcModules, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger<RpcModuleProvider>() ?? throw new ArgumentNullException(nameof(logManager));
            Serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
            _jsonRpcConfig = jsonRpcConfig ?? throw new ArgumentNullException(nameof(jsonRpcConfig));
            if (fileSystem.File.Exists(_jsonRpcConfig.CallsFilterFilePath))
            {
                if (_logger.IsWarn) _logger.Warn("Applying JSON RPC filter.");
                _filter = new RpcMethodFilter(_jsonRpcConfig.CallsFilterFilePath, fileSystem, _logger);
            }

            _registerMethod = GetType().GetMethods().First(m => m.Name == nameof(Register));
            foreach (RpcModuleInfo rpcModuleInfo in rpcModules)
            {
                RegisterNonGeneric(rpcModuleInfo.ModuleType, rpcModuleInfo.Pool);
                if (jsonRpcConfig.PreloadRpcModules) rpcModuleInfo.Pool.Preload();
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
                KeyValuePair<string, ResolvedMethodInfo>[] methods = GetMethods<T>(moduleType).ToArray();
                Func<bool, ValueTask<IRpcModule>> rentModule = canBeShared => RentModule(pool, canBeShared);
                Action<IRpcModule> returnModule = m => pool.ReturnModule((T)m);

                methods
                    .ForEach((method) =>
                    {
                        method.Value.SetPool(rentModule, returnModule, pool);
                        _methods[method.Key] = method.Value;
                    });
                _frozenMethods = null;

                _modules.Add(moduleType);

                if (_jsonRpcConfig.EnabledModules.Contains(moduleType, StringComparer.OrdinalIgnoreCase))
                {
                    _enabledModules.Add(moduleType);
                }
            }
        }

        private static ValueTask<IRpcModule> RentModule<T>(IRpcModulePool<T> pool, bool canBeShared) where T : IRpcModule
        {
            Task<T> moduleTask = pool.GetModule(canBeShared);
            return moduleTask.IsCompletedSuccessfully
                ? ValueTask.FromResult<IRpcModule>(moduleTask.Result)
                : AwaitRentModule(moduleTask);

            static async ValueTask<IRpcModule> AwaitRentModule(Task<T> moduleTask) =>
                await moduleTask;
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

        private void EnsureFrozenCollection() =>
            _frozenMethods ??= _methods.ToFrozenDictionary(StringComparer.Ordinal);

        public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module, out ResolvedMethodInfo? method)
        {
            EnsureFrozenCollection();
            module = null;
            method = null;

            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result))
            {
                return ModuleResolution.Unknown;
            }

            module = result.ModuleType;
            method = result;

            if ((result.Availability & context.RpcEndpoint) == RpcEndpoint.None)
            {
                return ModuleResolution.EndpointDisabled;
            }

            if (context.Url is not null)
            {
                return context.Url.EnabledModules.Contains(result.ModuleType)
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

        public ValueTask<IRpcModule> Rent(string methodName, bool canBeShared)
        {
            EnsureFrozenCollection();
            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result)) return ValueTask.FromResult<IRpcModule>(null!);

            return result.RentModule(canBeShared);
        }

        public ValueTask<IRpcModule> Rent(ResolvedMethodInfo method) => method.RentModule();

        public void Return(string methodName, IRpcModule rpcModule)
        {
            EnsureFrozenCollection();
            if (!_frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result))
                throw new InvalidOperationException("Not possible to return an unresolved module");

            result.ReturnModule(rpcModule);
        }

        public void Return(ResolvedMethodInfo method, IRpcModule rpcModule) => method.ReturnModule(rpcModule);

        public IRpcModulePool? GetPoolForMethod(string methodName)
        {
            EnsureFrozenCollection();
            return _frozenMethods.TryGetValue(methodName, out ResolvedMethodInfo result) ? result.ModulePool : null;
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
                    public readonly Type ParameterType;
                    public readonly JsonTypeInfo? TypeInfo;
                    public readonly ConstructorInvoker? ConstructorInvoker;
                    public readonly object? DefaultValue;
                    private readonly ParameterDetails _introspection;

                    public ParameterKind Kind { get; }
                    public bool IsNullable => (_introspection & ParameterDetails.IsNullable) != 0;
                    public bool IsIJsonRpcParam => ConstructorInvoker is not null;
                    public bool IsOptional => (_introspection & ParameterDetails.IsOptional) != 0;
                    public bool ReparseString => (_introspection & ParameterDetails.ReparseString) != 0;

                public IJsonRpcParam CreateRpcParam()
                {
                    ConstructorInvoker? constructorInvoker = ConstructorInvoker;
                    if (constructorInvoker is null)
                    {
                        ThrowNotJsonRpc();
                    }

                    return Unsafe.As<IJsonRpcParam>(constructorInvoker.Invoke([]));

                    [DoesNotReturn, StackTraceHidden]
                    static void ThrowNotJsonRpc() => throw new InvalidOperationException("This parameter is not an IJsonRpcParam");
                }

                internal ExpectedParameter(
                    ParameterInfo info,
                    Type parameterType,
                    JsonTypeInfo? typeInfo,
                    ConstructorInvoker? constructor,
                    ParameterKind kind,
                    object? defaultValue,
                    ParameterDetails introspection)
                {
                    ArgumentNullException.ThrowIfNull(info);

                    Info = info;
                    ParameterType = parameterType;
                    TypeInfo = typeInfo;
                    ConstructorInvoker = constructor;
                    DefaultValue = defaultValue;
                    Kind = kind;
                    _introspection = introspection;
                }
            }

            public enum ParameterKind
            {
                Typed,
                String,
                JsonElement,
                JsonRpcParam
            }

            [Flags]
            public enum ParameterDetails
            {
                None,
                IsNullable = 0b1,
                IsOptional = 0b10,
                ReparseString = 0b100,
            }

            public ResolvedMethodInfo() => ExpectedParameters = [];

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
                for (int i = 0; i < parameters.Length; i++)
                {
                    ParameterInfo parameter = parameters[i];
                    ConstructorInvoker? constructor = null;
                    ParameterDetails details = ParameterDetails.None;

                    Type paramType = parameter.ParameterType;
                    if (paramType.IsByRef)
                    {
                        paramType = paramType.GetElementType()!;
                    }

                    JsonTypeInfo? typeInfo = null;
                    ParameterKind kind = ParameterKind.Typed;

                    if (paramType.IsAssignableTo(typeof(IJsonRpcParam)))
                    {
                        ConstructorInfo constructorInfo = paramType.GetConstructor(BindingFlags.Public | BindingFlags.Instance, [])
                            ?? throw new InvalidOperationException($"{paramType.Name} must have parameterless constructor.");
                        constructor = ConstructorInvoker.Create(constructorInfo);
                        kind = ParameterKind.JsonRpcParam;
                    }
                    else if (paramType == typeof(string))
                    {
                        kind = ParameterKind.String;
                    }
                    else
                    {
                        if (paramType == typeof(System.Text.Json.JsonElement))
                        {
                            kind = ParameterKind.JsonElement;
                        }

                        EthereumJsonSerializer.JsonOptions.TryGetTypeInfo(paramType, out typeInfo);

                        JsonConverter converter = EthereumJsonSerializer.JsonOptions.GetConverter(paramType);
                        if (converter.GetType().Namespace?.StartsWith("System.", StringComparison.Ordinal) == true)
                        {
                            details |= ParameterDetails.ReparseString;
                        }
                    }

                    if (IsNullableParameter(parameter))
                    {
                        details |= ParameterDetails.IsNullable;
                    }
                    if (parameter.IsOptional)
                    {
                        details |= ParameterDetails.IsOptional;
                    }

                    object? defaultValue = parameter.IsOptional ? GetDefaultValue(parameter, paramType) : null;
                    expectedParameters[i] = new(parameter, paramType, typeInfo, constructor, kind, defaultValue, details);
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
            internal IRpcModulePool? ModulePool { get; private set; }

            public override string ToString() => MethodInfo.Name;

            internal void SetPool(
                Func<bool, ValueTask<IRpcModule>> rentModule,
                Action<IRpcModule> returnModule,
                IRpcModulePool modulePool)
            {
                _rentModule = rentModule;
                _returnModule = returnModule;
                ModulePool = modulePool;
            }

            private Func<bool, ValueTask<IRpcModule>>? _rentModule;

            private Action<IRpcModule>? _returnModule;

            internal ValueTask<IRpcModule> RentModule(bool canBeShared)
            {
                Func<bool, ValueTask<IRpcModule>>? rentModule = _rentModule;
                if (rentModule is null)
                {
                    ThrowMissingPool();
                }

                return rentModule(canBeShared);
            }

            internal ValueTask<IRpcModule> RentModule() => RentModule(ReadOnly);

            internal void ReturnModule(IRpcModule rpcModule)
            {
                Action<IRpcModule>? returnModule = _returnModule;
                if (returnModule is null)
                {
                    ThrowMissingPool();
                }

                returnModule(rpcModule);
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowMissingPool() =>
                throw new InvalidOperationException("No JSON-RPC module pool is attached to the resolved method.");

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

            private static object? GetDefaultValue(ParameterInfo parameter, Type parameterType)
            {
                object? defaultValue = parameter.DefaultValue;
                if (!ReferenceEquals(defaultValue, Type.Missing) && defaultValue != DBNull.Value)
                {
                    return defaultValue;
                }

                return parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null
                    ? Activator.CreateInstance(parameterType)
                    : null;
            }
        }
    }

    public record RpcModuleInfo(Type ModuleType, IRpcModulePool Pool);
}
