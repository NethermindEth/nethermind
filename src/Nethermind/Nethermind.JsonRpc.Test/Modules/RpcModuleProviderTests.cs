// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;
using Autofac;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Test.Modules;
using Nethermind.Era1.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Proof;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.Self)]
[TestFixture]
public class RpcModuleProviderTests
{
    private IRpcModuleProvider _moduleProvider = null!;
    private IFileSystem _fileSystem = null!;
    private JsonRpcContext _context = null!;

    [SetUp]
    public void Initialize()
    {
        _fileSystem = Substitute.For<IFileSystem>();
        _moduleProvider = CreateProvider(fileSystem: _fileSystem);
        _context = new JsonRpcContext(RpcEndpoint.Http);
    }

    [TearDown]
    public void TearDown() => _context?.Dispose();

    private static RpcModuleProvider CreateProvider(IJsonRpcConfig? config = null, IFileSystem? fileSystem = null, IReadOnlyList<RpcModuleInfo>? rpcModules = null) =>
        new(fileSystem ?? Substitute.For<IFileSystem>(), config ?? new JsonRpcConfig(), new EthereumJsonSerializer(), rpcModules ?? [], LimboLogs.Instance);

    [Test]
    public void Module_provider_will_recognize_disabled_modules()
    {
        JsonRpcConfig jsonRpcConfig = new() { EnabledModules = [] };
        _moduleProvider = CreateProvider(jsonRpcConfig);
        _moduleProvider.Register(new SingletonModulePool<IProofRpcModule>(Substitute.For<IProofRpcModule>(), false));
        _moduleProvider.Check("proof_call", _context).Should().Be(ModuleResolution.Disabled);
    }

    [Test]
    public void Method_resolution_is_case_sensitive()
    {
        SingletonModulePool<INetRpcModule> pool = new(new NetRpcModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
        _moduleProvider.Register(pool);

        _moduleProvider.Check("net_VeRsIoN", _context).Should().Be(ModuleResolution.Unknown);
        _moduleProvider.Check("net_Version", _context).Should().Be(ModuleResolution.Unknown);
        _moduleProvider.Check("Net_Version", _context).Should().Be(ModuleResolution.Unknown);
        _moduleProvider.Check("net_version", _context).Should().Be(ModuleResolution.Enabled);
    }

    [TestCase("eth_.*", ModuleResolution.Unknown)]
    [TestCase("net_.*", ModuleResolution.Enabled)]
    public void With_filter_can_reject(string regex, ModuleResolution expectedResult)
    {
        JsonRpcConfig config = new();
        _fileSystem.File.Exists(Arg.Any<string>()).Returns(true);
        _fileSystem.File.ReadLines(Arg.Any<string>()).Returns(new[] { regex });
        _moduleProvider = CreateProvider(config, _fileSystem);

        SingletonModulePool<INetRpcModule> pool = new(new NetRpcModule(LimboLogs.Instance, Substitute.For<INetBridge>()), true);
        _moduleProvider.Register(pool);

        ModuleResolution resolution = _moduleProvider.Check("net_version", _context);
        resolution.Should().Be(expectedResult);
    }

    [Test]
    public void Returns_politely_when_no_method_found()
    {
        SingletonModulePool<INetRpcModule> pool = new(Substitute.For<INetRpcModule>(), true);
        _moduleProvider.Register(pool);

        _moduleProvider.Check("unknown_method", _context).Should().Be(ModuleResolution.Unknown);
    }

    [Test]
    public void Method_resolution_is_scoped_to_url_enabled_modules()
    {
        _moduleProvider.Register(new SingletonModulePool<INetRpcModule>(Substitute.For<INetRpcModule>(), true));
        _moduleProvider.Register(new SingletonModulePool<IProofRpcModule>(Substitute.For<IProofRpcModule>(), true));

        JsonRpcUrl url = new("http", "127.0.0.1", 8888, RpcEndpoint.Http, false, new[] { "net" });

        ModuleResolution inScopeResolution = _moduleProvider.Check("net_version", JsonRpcContext.Http(url));
        Assert.That(inScopeResolution, Is.EqualTo(ModuleResolution.Enabled));

        ModuleResolution outOfScopeResolution = _moduleProvider.Check("proof_call", JsonRpcContext.Http(url));
        Assert.That(outOfScopeResolution, Is.EqualTo(ModuleResolution.Disabled));

        ModuleResolution fallbackResolution = _moduleProvider.Check("proof_call", new JsonRpcContext(RpcEndpoint.Http));
        Assert.That(fallbackResolution, Is.EqualTo(ModuleResolution.Enabled));
    }

    [Test]
    public void Allows_to_replace_modules()
    {
        SingletonModulePool<INetRpcModule> pool = new(Substitute.For<INetRpcModule>());
        _moduleProvider.Register(pool);
        _moduleProvider.GetPoolForMethod(nameof(INetRpcModule.net_listening)).Should().Be(pool);

        SingletonModulePool<INetRpcModule> pool2 = new(Substitute.For<INetRpcModule>());
        _moduleProvider.Register(pool2);

        _moduleProvider.GetPoolForMethod(nameof(INetRpcModule.net_listening)).Should().Be(pool2);
    }

    [TestCase("engine_newPayloadV4", ModuleType.Engine)]
    [TestCase("engine_getBlobsV2", ModuleType.Engine)]
    [TestCase("engine_forkchoiceUpdatedV3", ModuleType.Engine)]
    [TestCase("eth_call", ModuleType.Eth)]
    [TestCase("eth_getBlockByNumber", ModuleType.Eth)]
    [TestCase("eth_chainId", ModuleType.Eth)]
    public void Hot_method_resolution_matches_dictionary_resolution(string methodName, string moduleType)
    {
        RegisterHotModules();

        string internedMethodName = string.Intern(methodName);
        string nonInternedMethodName = new(methodName.ToCharArray());
        ReferenceEquals(nonInternedMethodName, internedMethodName).Should().BeFalse();

        _moduleProvider.Check(internedMethodName, _context, out string? internedModule, out RpcModuleProvider.ResolvedMethodInfo? internedMethod)
            .Should().Be(ModuleResolution.Enabled);
        _moduleProvider.Check(nonInternedMethodName, _context, out string? nonInternedModule, out RpcModuleProvider.ResolvedMethodInfo? nonInternedMethod)
            .Should().Be(ModuleResolution.Enabled);

        internedModule.Should().Be(moduleType);
        nonInternedModule.Should().Be(moduleType);
        internedMethod.Should().BeSameAs(nonInternedMethod);
        internedMethod!.ToString().Should().Be(methodName);
    }

    [Test]
    public void Hot_method_cache_updates_after_module_replacement()
    {
        TestModulePool<HotEngineRpcModule> firstPool = new(new HotEngineRpcModule());
        _moduleProvider.Register(firstPool);
        _moduleProvider.GetPoolForMethod("engine_newPayloadV4").Should().Be(firstPool);

        TestModulePool<HotEngineRpcModule> secondPool = new(new HotEngineRpcModule());
        _moduleProvider.Register(secondPool);

        _moduleProvider.GetPoolForMethod("engine_newPayloadV4").Should().Be(secondPool);
    }

    [Test]
    public async Task Caches_direct_invokers_for_result_wrapper_methods()
    {
        DirectInvokerRpcModule module = new();
        _moduleProvider.Register(new TestModulePool<DirectInvokerRpcModule>(module));

        RpcModuleProvider.ResolvedMethodInfo syncMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_sync))!;
        RpcModuleProvider.ResolvedMethodInfo asyncMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_async))!;
        RpcModuleProvider.ResolvedMethodInfo parameterMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_with_param))!;
        RpcModuleProvider.ResolvedMethodInfo fourParameterMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_with_four_params))!;
        RpcModuleProvider.ResolvedMethodInfo requiredValueParameterMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_required_value_param))!;
        RpcModuleProvider.ResolvedMethodInfo typedErrorDataMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_typed_error_data))!;
        RpcModuleProvider.ResolvedMethodInfo asyncTypedErrorDataMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_async_typed_error_data))!;
        RpcModuleProvider.ResolvedMethodInfo polymorphicMethod = _moduleProvider.Resolve(nameof(DirectInvokerRpcModule.direct_polymorphic))!;

        syncMethod.DirectNoParameterInvoker.Should().NotBeNull();
        asyncMethod.DirectNoParameterInvoker.Should().NotBeNull();
        parameterMethod.DirectNoParameterInvoker.Should().BeNull();
        parameterMethod.DirectParameterInvoker.Should().NotBeNull();
        fourParameterMethod.DirectParameterInvoker.Should().NotBeNull();
        requiredValueParameterMethod.DirectParameterInvoker.Should().BeNull();
        requiredValueParameterMethod.ExpectedParameters[0].TypeInfo.Should().NotBeNull();
        RpcParameterTypeInfo<int>.Get().Should().NotBeNull();

        AssertResultMetadata(syncMethod, typeof(ResultWrapper<string>), typeof(string));
        AssertResultMetadata(asyncMethod, typeof(ResultWrapper<long>), typeof(long), isTaskWrapped: true);
        AssertResultMetadata(typedErrorDataMethod, typeof(ResultWrapper<string, bool>), typeof(string), typeof(bool));
        AssertResultMetadata(asyncTypedErrorDataMethod, typeof(ResultWrapper<string, string>), typeof(string), typeof(string), isTaskWrapped: true);
        AssertResultMetadata(polymorphicMethod, typeof(ResultWrapper<PolymorphicPayload>), typeof(PolymorphicPayload), successPayloadCanHaveDerivedRuntimeType: true);

        IResultWrapper syncResult = syncMethod.DirectNoParameterInvoker!(module).Should().BeAssignableTo<IResultWrapper>().Subject;
        syncResult.Data.Should().Be("sync");

        Task<ResultWrapper<long>> asyncResult = asyncMethod.DirectNoParameterInvoker!(module).Should().BeAssignableTo<Task<ResultWrapper<long>>>().Subject;
        (await asyncResult).Data.Should().Be(5);
        asyncMethod.ReadTaskResult(asyncResult)!.Data.Should().Be(5);

        IResultWrapper parameterResult = parameterMethod.DirectParameterInvoker!(module, ["param"]).Should().BeAssignableTo<IResultWrapper>().Subject;
        parameterResult.Data.Should().Be("param");

        IResultWrapper fourParameterResult = fourParameterMethod.DirectParameterInvoker!(module, ["a", "b", "c", "d"]).Should().BeAssignableTo<IResultWrapper>().Subject;
        fourParameterResult.Data.Should().Be("abcd");

        Task<ResultWrapper<string, string>> asyncTypedErrorResult = asyncTypedErrorDataMethod.DirectNoParameterInvoker!(module)
            .Should().BeAssignableTo<Task<ResultWrapper<string, string>>>().Subject;
        asyncTypedErrorDataMethod.ReadTaskResult(asyncTypedErrorResult)!.Data.Should().Be("error-data");

        module.SyncCalls.Should().Be(1);
        module.AsyncCalls.Should().Be(1);
        module.ParameterCalls.Should().Be(1);
        module.FourParameterCalls.Should().Be(1);
    }

    [Test]
    public void Rpc_payload_type_info_caches_generated_metadata_and_resolves_fallbacks()
    {
        JsonTypeInfo<PayloadStatusV1> firstGeneratedLookup = RpcPayloadTypeInfo<PayloadStatusV1>.Get(EthereumJsonSerializer.JsonOptions);
        JsonTypeInfo<PayloadStatusV1> secondGeneratedLookup = RpcPayloadTypeInfo<PayloadStatusV1>.Get(EthereumJsonSerializer.JsonOptions);
        RpcPayloadTypeInfo<FallbackPayload>.Get(EthereumJsonSerializer.JsonOptions).Should().NotBeNull();

        firstGeneratedLookup.Should().BeSameAs(secondGeneratedLookup);
    }

    [Test]
    public void Generated_rpc_type_info_covers_json_rpc_assembly_modules()
    {
        List<string> missing = [];
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.Consensus.Clique.ICliqueRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.Era1.JsonRpc.IEraAdminRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.EraE.JsonRpc.IEraAdminRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.Flashbots.Modules.Flashbots.IFlashbotsRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.HealthChecks.IHealthRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(IEthRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(IEngineRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.Optimism.Rpc.IOptimismEngineRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.StateComposition.Rpc.IStateCompositionRpcModule).Assembly, missing);
        AddMissingAssemblyPayloadTypes(typeof(Nethermind.Taiko.Rpc.ITaikoEngineRpcModule).Assembly, missing);

        missing.Should().BeEmpty();
    }

    private static void AddMissingAssemblyPayloadTypes(Assembly assembly, List<string> missing)
    {
        Type[] assemblyTypes = assembly.GetTypes();
        for (int i = 0; i < assemblyTypes.Length; i++)
        {
            Type moduleType = assemblyTypes[i];
            if (!typeof(IRpcModule).IsAssignableFrom(moduleType))
            {
                continue;
            }

            AddMissingModulePayloadTypes(moduleType, missing);
        }
    }

    [Test]
    public void Can_register_via_constructor()
    {
        IRpcModuleProvider moduleProvider = CreateEraAdminModuleProvider();
        moduleProvider.Check("admin_exportHistory", _context).Should().Be(ModuleResolution.Enabled);
    }

    [Test]
    public async Task Can_register_multiple_module_interface_of_same_rpc_module()
    {
        IRpcModuleProvider moduleProvider = CreateEraAdminModuleProvider();

        moduleProvider.RegisterBounded<IAdminRpcModule>(new SingletonFactory<IAdminRpcModule>(Substitute.For<IAdminRpcModule>()), 1, Int32.MaxValue);

        moduleProvider.Check("admin_exportHistory", _context).Should().Be(ModuleResolution.Enabled);
        moduleProvider.Check("admin_addPeer", _context).Should().Be(ModuleResolution.Enabled);

        IRpcModule adminClass = await moduleProvider.Rent("admin_addPeer", true);
        (adminClass is IAdminRpcModule).Should().BeTrue();
        IRpcModule historyClass = await moduleProvider.Rent("admin_exportHistory", true);
        (historyClass is IEraAdminRpcModule).Should().BeTrue();
    }

    [TestCase(true)]
    [TestCase(false)]
    public void ModuleFactory_FromDI_IsLazy(bool preload)
    {
        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new JsonRpcConfig { PreloadRpcModules = preload }))
            .AddSingleton<TestRpcModuleDependencies>()
            .RegisterSingletonJsonRpcModule<ITestRpcModule, TestRpcModule>()
            .Build();

        _ = container.Resolve<IRpcModuleProvider>();

        container.Resolve<TestRpcModuleDependencies>().WasRequested.Should().Be(preload);
    }

    private static IRpcModuleProvider CreateEraAdminModuleProvider() =>
        CreateProvider(
            new JsonRpcConfig { EnabledModules = [ModuleType.Admin] },
            rpcModules:
            [
                new RpcModuleInfo(typeof(IEraAdminRpcModule), new SingletonModulePool<IEraAdminRpcModule>(Substitute.For<IEraAdminRpcModule>()))
            ]);

    private void RegisterHotModules()
    {
        JsonRpcConfig config = new() { EnabledModules = [ModuleType.Engine, ModuleType.Eth] };
        _moduleProvider = CreateProvider(config, _fileSystem);
        _moduleProvider.Register(new TestModulePool<HotEngineRpcModule>(new HotEngineRpcModule()));
        _moduleProvider.Register(new TestModulePool<HotEthRpcModule>(new HotEthRpcModule()));
    }

    private static void AssertResultMetadata(
        RpcModuleProvider.ResolvedMethodInfo method,
        Type resultWrapperType,
        Type successPayloadType,
        Type? errorDataPayloadType = null,
        bool isTaskWrapped = false,
        bool successPayloadCanHaveDerivedRuntimeType = false)
    {
        method.IsTaskWrapped.Should().Be(isTaskWrapped);
        method.ResultWrapperType.Should().Be(resultWrapperType);
        method.SuccessPayloadType.Should().Be(successPayloadType);
        method.SuccessPayloadTypeInfo.Should().NotBeNull();
        method.SuccessPayloadCanHaveDerivedRuntimeType.Should().Be(successPayloadCanHaveDerivedRuntimeType);
        method.ErrorDataPayloadType.Should().Be(errorDataPayloadType);
        method.ErrorDataPayloadCanHaveDerivedRuntimeType.Should().BeFalse();
        (method.TaskResultAccessor is not null).Should().Be(isTaskWrapped);

        if (errorDataPayloadType is not null)
        {
            method.ErrorDataPayloadTypeInfo.Should().NotBeNull();
        }
    }

    private static void AddMissingModulePayloadTypes(Type moduleType, List<string> missing)
    {
        AddMissingMethodPayloadTypes(moduleType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), missing);

        Type[] interfaceTypes = moduleType.GetInterfaces();
        for (int i = 0; i < interfaceTypes.Length; i++)
        {
            Type interfaceType = interfaceTypes[i];
            if (typeof(IRpcModule).IsAssignableFrom(interfaceType))
            {
                AddMissingMethodPayloadTypes(interfaceType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), missing);
            }
        }
    }

    private static void AddMissingMethodPayloadTypes(MethodInfo[] methods, List<string> missing)
    {
        for (int i = 0; i < methods.Length; i++)
        {
            MethodInfo method = methods[i];
            if (method.IsStatic || method.IsSpecialName || method.ContainsGenericParameters)
            {
                continue;
            }

            AddMissingResultPayloadTypes(method, missing);
            ParameterInfo[] parameters = method.GetParameters();
            for (int j = 0; j < parameters.Length; j++)
            {
                AddMissingPayloadType(method, $"parameter {parameters[j].Name}", parameters[j].ParameterType, missing);
            }
        }
    }

    private static void AddMissingResultPayloadTypes(MethodInfo method, List<string> missing)
    {
        Type resultType = UnwrapTaskLike(method.ReturnType);
        if (!resultType.IsGenericType)
        {
            return;
        }

        Type genericTypeDefinition = resultType.GetGenericTypeDefinition();
        if (genericTypeDefinition == typeof(ResultWrapper<>))
        {
            AddMissingPayloadType(method, "result", resultType.GetGenericArguments()[0], missing);
            return;
        }

        if (genericTypeDefinition == typeof(ResultWrapper<,>))
        {
            Type[] genericArguments = resultType.GetGenericArguments();
            AddMissingPayloadType(method, "result", genericArguments[0], missing);
            AddMissingPayloadType(method, "error data", genericArguments[1], missing);
        }
    }

    private static Type UnwrapTaskLike(Type type)
    {
        if (type.IsGenericType)
        {
            Type genericTypeDefinition = type.GetGenericTypeDefinition();
            if (genericTypeDefinition == typeof(Task<>) || genericTypeDefinition == typeof(ValueTask<>))
            {
                return type.GetGenericArguments()[0];
            }
        }

        return type;
    }

    private static void AddMissingPayloadType(MethodInfo method, string role, Type type, List<string> missing)
    {
        if (type.IsByRef)
        {
            type = type.GetElementType()!;
        }

        if (type.ContainsGenericParameters || type == typeof(void))
        {
            return;
        }

        if (!GeneratedRpcTypeInfo.TryGet(type, out _))
        {
            missing.Add($"{method.DeclaringType?.FullName}.{method.Name} {role}: {type.FullName ?? type.Name}");
        }
    }

    [RpcModule(ModuleType.Engine)]
    private sealed class HotEngineRpcModule : IRpcModule
    {
        public ResultWrapper<string> engine_newPayloadV4() => ResultWrapper<string>.Success(string.Empty);

        public ResultWrapper<string> engine_getBlobsV2() => ResultWrapper<string>.Success(string.Empty);

        public ResultWrapper<string> engine_forkchoiceUpdatedV3() => ResultWrapper<string>.Success(string.Empty);
    }

    [RpcModule(ModuleType.Eth)]
    private sealed class HotEthRpcModule : IRpcModule
    {
        public ResultWrapper<HexBytes> eth_call() => ResultWrapper<HexBytes>.Success(default);

        public ResultWrapper<string> eth_getBlockByNumber() => ResultWrapper<string>.Success(string.Empty);

        public ResultWrapper<string> eth_chainId() => ResultWrapper<string>.Success(string.Empty);
    }

    [RpcModule(ModuleType.Net)]
    private sealed class DirectInvokerRpcModule : IRpcModule
    {
        public int SyncCalls { get; private set; }
        public int AsyncCalls { get; private set; }
        public int ParameterCalls { get; private set; }
        public int FourParameterCalls { get; private set; }

        public ResultWrapper<string> direct_sync()
        {
            SyncCalls++;
            return ResultWrapper<string>.Success("sync");
        }

        public Task<ResultWrapper<long>> direct_async()
        {
            AsyncCalls++;
            return Task.FromResult(ResultWrapper<long>.Success(5));
        }

        public ResultWrapper<string> direct_with_param(string value)
        {
            ParameterCalls++;
            return ResultWrapper<string>.Success(value);
        }

        public ResultWrapper<string> direct_with_four_params(string first, string second, string third, string fourth)
        {
            FourParameterCalls++;
            return ResultWrapper<string>.Success(first + second + third + fourth);
        }

        public ResultWrapper<int> direct_required_value_param(int value) => ResultWrapper<int>.Success(value);

        public ResultWrapper<string, bool> direct_typed_error_data() =>
            ResultWrapper<string, bool>.Fail("typed", ErrorCodes.InvalidParams, false);

        public Task<ResultWrapper<string, string>> direct_async_typed_error_data() =>
            Task.FromResult(ResultWrapper<string, string>.Fail("typed", ErrorCodes.InvalidParams, "error-data"));

        public ResultWrapper<PolymorphicPayload> direct_polymorphic() =>
            ResultWrapper<PolymorphicPayload>.Success(new PolymorphicPayload());
    }

    [RpcModule(ModuleType.Eth)]
    private interface ITestRpcModule : IRpcModule { }

    private class TestRpcModuleDependencies { internal bool WasRequested; }

    private class TestRpcModule : ITestRpcModule { public TestRpcModule(TestRpcModuleDependencies dependencies) => dependencies.WasRequested = true; }

    private sealed class FallbackPayload { public string? Value { get; set; } }

    private class PolymorphicPayload { }

    private sealed class TestModulePool<T>(T module) : IRpcModulePool<T> where T : IRpcModule
    {
        public IRpcModuleFactory<T> Factory { get; } = new TestModuleFactory(module);

        public Task<T> GetModule(bool canBeShared) => Task.FromResult(module);

        public void ReturnModule(T module) { }

        private sealed class TestModuleFactory(T module) : IRpcModuleFactory<T>
        {
            public T Create() => module;
        }
    }
}
