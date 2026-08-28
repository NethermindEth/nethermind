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
using System.Text.Json;
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

        private static readonly string[] HotMethodNames =
        [
            "engine_newPayloadV4", "engine_getBlobsV2", "engine_forkchoiceUpdatedV3",
            "eth_call", "eth_getBlockByNumber", "eth_chainId"
        ];

        private Dictionary<string, ResolvedMethodInfo> _methods = [];
        private MethodCache? _methodCache;

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

        private void RegisterNonGeneric(Type moduleType, IRpcModulePool pool) =>
            _registerMethod.MakeGenericMethod(moduleType).Invoke(this, [pool]);

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
                Dictionary<string, ResolvedMethodInfo> methodsByName = new(_methods, StringComparer.Ordinal);
                KeyValuePair<string, ResolvedMethodInfo>[] methods = GetMethods<T>(moduleType).ToArray();
                Func<bool, ValueTask<IRpcModule>> rentModule = canBeShared => RentModule(pool, canBeShared);
                Action<IRpcModule> returnModule = m => pool.ReturnModule((T)m);

                methods
                    .ForEach((method) =>
                    {
                        method.Value.SetPool(rentModule, returnModule, pool);
                        methodsByName[method.Key] = method.Value;
                    });

                _modules.Add(moduleType);

                if (_jsonRpcConfig.EnabledModules.Contains(moduleType, StringComparer.OrdinalIgnoreCase))
                {
                    _enabledModules.Add(moduleType);
                }

                _methods = methodsByName;
                Volatile.Write(ref _methodCache, null);
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

        private MethodCache EnsureMethodCache()
        {
            MethodCache? cache = Volatile.Read(ref _methodCache);
            if (cache is not null)
            {
                return cache;
            }

            lock (_updateRegistrationsLock)
            {
                cache = Volatile.Read(ref _methodCache);
                if (cache is not null)
                {
                    return cache;
                }

                cache = BuildMethodCache(_methods);
                Volatile.Write(ref _methodCache, cache);
                return cache;
            }

            static MethodCache BuildMethodCache(Dictionary<string, ResolvedMethodInfo> methods)
            {
                FrozenDictionary<string, ResolvedMethodInfo> frozenMethods = methods.ToFrozenDictionary(StringComparer.Ordinal);
                ResolvedMethodInfo?[] hotMethods = new ResolvedMethodInfo?[HotMethodNames.Length];
                for (int i = 0; i < HotMethodNames.Length; i++)
                {
                    hotMethods[i] = Resolve(frozenMethods, HotMethodNames[i]);
                }

                return new MethodCache(frozenMethods, hotMethods);
            }

            static ResolvedMethodInfo? Resolve(FrozenDictionary<string, ResolvedMethodInfo> methods, string methodName) =>
                methods.TryGetValue(methodName, out ResolvedMethodInfo result) ? result : null;
        }

        private static bool TryGetResolvedMethod(MethodCache cache, string methodName, [NotNullWhen(true)] out ResolvedMethodInfo? method)
        {
            method = TryGetInternedHotMethod(cache, methodName);
            if (method is not null)
            {
                return true;
            }

            if (cache.Methods.TryGetValue(methodName, out ResolvedMethodInfo result))
            {
                method = result;
                return true;
            }

            method = null;
            return false;
        }

        private static ResolvedMethodInfo? TryGetInternedHotMethod(MethodCache cache, string methodName)
        {
            for (int i = 0; i < HotMethodNames.Length; i++)
            {
                if (ReferenceEquals(methodName, HotMethodNames[i]))
                {
                    return cache.HotMethods[i];
                }
            }

            return null;
        }

        public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module, out ResolvedMethodInfo? method)
        {
            MethodCache cache = EnsureMethodCache();
            module = null;
            method = null;

            if (!TryGetResolvedMethod(cache, methodName, out ResolvedMethodInfo? result))
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
            MethodCache cache = EnsureMethodCache();
            if (!TryGetResolvedMethod(cache, methodName, out ResolvedMethodInfo? result)) return null;

            return result;
        }

        public ValueTask<IRpcModule> Rent(string methodName, bool canBeShared)
        {
            MethodCache cache = EnsureMethodCache();
            if (!TryGetResolvedMethod(cache, methodName, out ResolvedMethodInfo? result)) return ValueTask.FromResult<IRpcModule>(null!);

            return result.RentModule(canBeShared);
        }

        public ValueTask<IRpcModule> Rent(ResolvedMethodInfo method) => method.RentModule();

        public void Return(string methodName, IRpcModule rpcModule)
        {
            MethodCache cache = EnsureMethodCache();
            if (!TryGetResolvedMethod(cache, methodName, out ResolvedMethodInfo? result))
                throw new InvalidOperationException("Not possible to return an unresolved module");

            result.ReturnModule(rpcModule);
        }

        public void Return(ResolvedMethodInfo method, IRpcModule rpcModule) => method.ReturnModule(rpcModule);

        private sealed class MethodCache(
            FrozenDictionary<string, ResolvedMethodInfo> methods,
            ResolvedMethodInfo?[] hotMethods)
        {
            public FrozenDictionary<string, ResolvedMethodInfo> Methods { get; } = methods;
            public ResolvedMethodInfo?[] HotMethods { get; } = hotMethods;
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
            private const BindingFlags NonPublicStatic = BindingFlags.NonPublic | BindingFlags.Static;
            private static readonly MethodInfo _createTypedDirectNoParameterInvokerMethod = GetStaticMethod(nameof(CreateTypedDirectNoParameterInvoker));
            private static readonly MethodInfo _createTypedDirectOneParameterInvokerMethod = GetStaticMethod(nameof(CreateTypedDirectOneParameterInvoker));
            private static readonly MethodInfo _createTypedDirectTwoParameterInvokerMethod = GetStaticMethod(nameof(CreateTypedDirectTwoParameterInvoker));
            private static readonly MethodInfo _createTypedDirectThreeParameterInvokerMethod = GetStaticMethod(nameof(CreateTypedDirectThreeParameterInvoker));
            private static readonly MethodInfo _createTypedDirectFourParameterInvokerMethod = GetStaticMethod(nameof(CreateTypedDirectFourParameterInvoker));
            private static readonly MethodInfo _readTaskResultMethod = GetStaticMethod(nameof(ReadTaskResult));

            internal delegate IResultWrapper? TaskResultReader(Task task);

            private static MethodInfo GetStaticMethod(string methodName) =>
                typeof(ResolvedMethodInfo).GetMethod(methodName, NonPublicStatic)!;

            public readonly struct ExpectedParameter
            {
                public readonly ParameterInfo Info;
                public readonly Type ParameterType;
                public readonly JsonTypeInfo? TypeInfo;
                public readonly ConstructorInvoker? ConstructorInvoker;
                public readonly object? DefaultValue;
                internal readonly bool HasParameterConverter;
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
                    bool hasParameterConverter,
                    ParameterDetails introspection)
                {
                    ArgumentNullException.ThrowIfNull(info);

                    Info = info;
                    ParameterType = parameterType;
                    TypeInfo = typeInfo;
                    ConstructorInvoker = constructor;
                    DefaultValue = defaultValue;
                    HasParameterConverter = hasParameterConverter;
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
                    Type? converterType = null;
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

                        converterType = parameter.GetCustomAttribute<JsonRpcParameterAttribute>()?.ConverterType;
                        typeInfo = converterType is null
                            ? RpcParameterTypeInfo.Get(paramType)
                            : CreateParameterTypeInfo(parameter, paramType, converterType);

                        if (ShouldReparseStringParameter(paramType, EthereumJsonSerializer.JsonOptions.GetConverter(paramType)))
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
                    expectedParameters[i] = new(parameter, paramType, typeInfo, constructor, kind, defaultValue, converterType is not null, details);
                }

                ExpectedParameters = expectedParameters;
                ReadOnly = readOnly;
                Availability = availability;
                IsTaskWrapped = TryGetTaskResultType(methodInfo.ReturnType, out Type? taskResultType);
                ResultWrapperType = IsTaskWrapped ? taskResultType : methodInfo.ReturnType;
                if (!ResultWrapperType.IsAssignableTo(typeof(IResultWrapper)))
                {
                    ResultWrapperType = null;
                }

                if (ResultWrapperType is not null)
                {
                    SuccessPayloadType = GetResultWrapperPayloadType(ResultWrapperType);
                    ErrorDataPayloadType = GetResultWrapperErrorDataType(ResultWrapperType);
                    SuccessPayloadTypeInfo = GetJsonTypeInfo(SuccessPayloadType);
                    ErrorDataPayloadTypeInfo = GetJsonTypeInfo(ErrorDataPayloadType);
                    SuccessPayloadCanHaveDerivedRuntimeType = RpcPayloadTypeShape.CanHaveDerivedRuntimeType(SuccessPayloadType);
                    ErrorDataPayloadCanHaveDerivedRuntimeType = RpcPayloadTypeShape.CanHaveDerivedRuntimeType(ErrorDataPayloadType);

                    if (IsTaskWrapped)
                    {
                        TaskResultAccessor = CreateTaskResultAccessor(ResultWrapperType);
                    }
                }

                Invoker = MethodInvoker.Create(methodInfo);
                DirectNoParameterInvoker = CreateDirectNoParameterInvoker(methodInfo, parameters.Length);
                DirectParameterInvoker = CreateDirectParameterInvoker(methodInfo, parameters);
            }

            public string ModuleType { get; }
            public MethodInfo MethodInfo { get; }
            public MethodInvoker Invoker { get; }
            public Func<IRpcModule, object?>? DirectNoParameterInvoker { get; }
            public Func<IRpcModule, object?[], object?>? DirectParameterInvoker { get; }
            public ExpectedParameter[] ExpectedParameters { get; }
            public bool ReadOnly { get; }
            public RpcEndpoint Availability { get; }
            internal Type? ResultWrapperType { get; }
            internal Type? SuccessPayloadType { get; }
            internal Type? ErrorDataPayloadType { get; }
            internal JsonTypeInfo? SuccessPayloadTypeInfo { get; }
            internal JsonTypeInfo? ErrorDataPayloadTypeInfo { get; }
            internal bool SuccessPayloadCanHaveDerivedRuntimeType { get; }
            internal bool ErrorDataPayloadCanHaveDerivedRuntimeType { get; }
            internal bool IsTaskWrapped { get; }
            internal TaskResultReader? TaskResultAccessor { get; }
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

            private static Func<IRpcModule, object?>? CreateDirectNoParameterInvoker(MethodInfo methodInfo, int parameterCount)
            {
                if (parameterCount != 0 || !CanUseDirectInvokerReturn(methodInfo.ReturnType))
                {
                    return null;
                }

                Type? declaringType = methodInfo.DeclaringType;
                return declaringType is null
                    ? null
                    : (Func<IRpcModule, object?>)_createTypedDirectNoParameterInvokerMethod
                        .MakeGenericMethod(declaringType, methodInfo.ReturnType)
                        .Invoke(null, [methodInfo])!;
            }

            private static Func<IRpcModule, object?> CreateTypedDirectNoParameterInvoker<TModule, TResult>(MethodInfo methodInfo)
            {
                Func<TModule, TResult> typedInvoker = methodInfo.CreateDelegate<Func<TModule, TResult>>();
                return module => typedInvoker((TModule)module);
            }

            private static Func<IRpcModule, object?[], object?>? CreateDirectParameterInvoker(MethodInfo methodInfo, ParameterInfo[] parameters)
            {
                if (!CanUseDirectParameterInvoker(methodInfo.ReturnType, parameters))
                {
                    return null;
                }

                Type? declaringType = methodInfo.DeclaringType;
                if (declaringType is null)
                {
                    return null;
                }

                return parameters.Length switch
                {
                    1 => (Func<IRpcModule, object?[], object?>)_createTypedDirectOneParameterInvokerMethod
                        .MakeGenericMethod(declaringType, parameters[0].ParameterType, methodInfo.ReturnType)
                        .Invoke(null, [methodInfo])!,
                    2 => (Func<IRpcModule, object?[], object?>)_createTypedDirectTwoParameterInvokerMethod
                        .MakeGenericMethod(declaringType, parameters[0].ParameterType, parameters[1].ParameterType, methodInfo.ReturnType)
                        .Invoke(null, [methodInfo])!,
                    3 => (Func<IRpcModule, object?[], object?>)_createTypedDirectThreeParameterInvokerMethod
                        .MakeGenericMethod(declaringType, parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, methodInfo.ReturnType)
                        .Invoke(null, [methodInfo])!,
                    4 => (Func<IRpcModule, object?[], object?>)_createTypedDirectFourParameterInvokerMethod
                        .MakeGenericMethod(declaringType, parameters[0].ParameterType, parameters[1].ParameterType, parameters[2].ParameterType, parameters[3].ParameterType, methodInfo.ReturnType)
                        .Invoke(null, [methodInfo])!,
                    _ => null,
                };
            }

            private static Func<IRpcModule, object?[], object?> CreateTypedDirectOneParameterInvoker<TModule, T1, TResult>(MethodInfo methodInfo)
            {
                Func<TModule, T1, TResult> typedInvoker = methodInfo.CreateDelegate<Func<TModule, T1, TResult>>();
                return (module, parameters) => typedInvoker((TModule)module, (T1)parameters[0]!);
            }

            private static Func<IRpcModule, object?[], object?> CreateTypedDirectTwoParameterInvoker<TModule, T1, T2, TResult>(MethodInfo methodInfo)
            {
                Func<TModule, T1, T2, TResult> typedInvoker = methodInfo.CreateDelegate<Func<TModule, T1, T2, TResult>>();
                return (module, parameters) => typedInvoker((TModule)module, (T1)parameters[0]!, (T2)parameters[1]!);
            }

            private static Func<IRpcModule, object?[], object?> CreateTypedDirectThreeParameterInvoker<TModule, T1, T2, T3, TResult>(MethodInfo methodInfo)
            {
                Func<TModule, T1, T2, T3, TResult> typedInvoker = methodInfo.CreateDelegate<Func<TModule, T1, T2, T3, TResult>>();
                return (module, parameters) => typedInvoker((TModule)module, (T1)parameters[0]!, (T2)parameters[1]!, (T3)parameters[2]!);
            }

            private static Func<IRpcModule, object?[], object?> CreateTypedDirectFourParameterInvoker<TModule, T1, T2, T3, T4, TResult>(MethodInfo methodInfo)
            {
                Func<TModule, T1, T2, T3, T4, TResult> typedInvoker = methodInfo.CreateDelegate<Func<TModule, T1, T2, T3, T4, TResult>>();
                return (module, parameters) => typedInvoker((TModule)module, (T1)parameters[0]!, (T2)parameters[1]!, (T3)parameters[2]!, (T4)parameters[3]!);
            }

            private static bool CanUseDirectParameterInvoker(Type returnType, ParameterInfo[] parameters)
            {
                if (parameters.Length is 0 or > 4 || !CanUseDirectInvokerReturn(returnType))
                {
                    return false;
                }

                for (int i = 0; i < parameters.Length; i++)
                {
                    Type parameterType = parameters[i].ParameterType;
                    if (parameterType.IsByRef ||
                        parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null && !parameters[i].IsOptional)
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool CanUseDirectInvokerReturn(Type returnType) =>
                returnType.IsAssignableTo(typeof(IResultWrapper)) ||
                TryGetTaskResultType(returnType, out Type? resultType) && resultType.IsAssignableTo(typeof(IResultWrapper));

            private static bool ShouldReparseStringParameter(Type parameterType, JsonConverter converter) =>
                converter.GetType().Namespace?.StartsWith("System.", StringComparison.Ordinal) == true ||
                parameterType.IsArray && parameterType != typeof(byte[]);

            private static JsonTypeInfo CreateParameterTypeInfo(ParameterInfo parameter, Type parameterType, Type converterType)
            {
                if (!typeof(JsonConverter).IsAssignableFrom(converterType)
                    || Activator.CreateInstance(converterType, nonPublic: true) is not JsonConverter converter
                    || !converter.CanConvert(parameterType))
                {
                    throw new InvalidOperationException(
                        $"{converterType.FullName} is not a JSON converter for parameter {parameter.Name} of type {parameterType.FullName}.");
                }

                JsonSerializerOptions options = new(EthereumJsonSerializer.JsonRpcRequestOptions);
                options.Converters.Insert(0, converter);
                return options.GetTypeInfo(parameterType);
            }

            internal IResultWrapper? ReadTaskResult(Task task) => TaskResultAccessor?.Invoke(task);

            private static TaskResultReader CreateTaskResultAccessor(Type resultType) =>
                _readTaskResultMethod.MakeGenericMethod(resultType).CreateDelegate<TaskResultReader>();

            private static IResultWrapper? ReadTaskResult<TResult>(Task task)
                where TResult : IResultWrapper =>
                ((Task<TResult>)task).Result;

            private static bool TryGetTaskResultType(Type taskType, [NotNullWhen(true)] out Type? resultType)
            {
                for (Type? type = taskType; type is not null; type = type.BaseType)
                {
                    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
                    {
                        resultType = type.GetGenericArguments()[0];
                        return true;
                    }
                }

                resultType = null;
                return false;
            }

            private static Type? GetResultWrapperPayloadType(Type resultWrapperType)
            {
                for (Type? type = resultWrapperType; type is not null; type = type.BaseType)
                {
                    if (!type.IsGenericType)
                    {
                        continue;
                    }

                    Type genericTypeDefinition = type.GetGenericTypeDefinition();
                    if (genericTypeDefinition == typeof(ResultWrapper<>) || genericTypeDefinition == typeof(ResultWrapper<,>))
                    {
                        return type.GetGenericArguments()[0];
                    }
                }

                return null;
            }

            private static Type? GetResultWrapperErrorDataType(Type resultWrapperType)
            {
                for (Type? type = resultWrapperType; type is not null; type = type.BaseType)
                {
                    if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ResultWrapper<,>))
                    {
                        return type.GetGenericArguments()[1];
                    }
                }

                return null;
            }

            private static JsonTypeInfo? GetJsonTypeInfo(Type? payloadType)
            {
                if (payloadType is null)
                {
                    return null;
                }

                EthereumJsonSerializer.JsonOptions.TryGetTypeInfo(payloadType, out JsonTypeInfo? typeInfo);
                return typeInfo;
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
