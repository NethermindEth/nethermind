// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Autofac.Core.Lifetime;
using FluentAssertions;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Era1;
using Nethermind.Evm;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Flashbots;
using Nethermind.Hive;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum;
using Nethermind.Optimism;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Taiko.TaikoSpec;
using NSubstitute;
using NUnit.Framework;
using Build = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Runner.Test;

[TestFixture, Parallelizable(ParallelScope.None)]
public class EthereumRunnerTests
{
    static EthereumRunnerTests()
    {
        AssemblyLoadContext.Default.Resolving += static (_, _) => null;
    }

    private static readonly Lazy<ICollection>? _cachedProviders = new(InitOnce);

    private static ICollection InitOnce()
    {
        // we need this to discover ChainSpecEngineParameters
        _ = new[] { typeof(CliqueChainSpecEngineParameters), typeof(OptimismChainSpecEngineParameters), typeof(TaikoChainSpecEngineParameters) };

        // by pre-caching configs providers we make the tests do lot less work
        ConcurrentQueue<(string, ConfigProvider)> result = new();
        Parallel.ForEach(Directory.GetFiles("configs"), configFile =>
        {
            var configProvider = new ConfigProvider();
            configProvider.AddSource(new JsonConfigSource(configFile));
            configProvider.Initialize();
            result.Enqueue((configFile, configProvider));
        });

        {
            // Special case for verify trie on state sync finished
            var configProvider = new ConfigProvider();
            configProvider.AddSource(new JsonConfigSource("configs/mainnet.json"));
            configProvider.Initialize();
            configProvider.GetConfig<ISyncConfig>().VerifyTrieOnStateSyncFinished = true;
            result.Enqueue(("mainnet-verify-trie-starter", configProvider));
        }

        {
            // Flashbots
            var configProvider = new ConfigProvider();
            configProvider.AddSource(new JsonConfigSource("configs/mainnet.json"));
            configProvider.Initialize();
            configProvider.GetConfig<IFlashbotsConfig>().Enabled = true;
            result.Enqueue(("flashbots", configProvider));
        }

        return result;
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        // Optimism override decoder globally, which mess up other test
        Assembly? assembly = Assembly.GetAssembly(typeof(NetworkNodeDecoder));
        if (assembly is not null)
        {
            Rlp.RegisterDecoders(assembly, true);
        }
    }

    public static IEnumerable ChainSpecRunnerTests
    {
        get
        {
            int index = 0;
            foreach (var cachedProvider in _cachedProviders!.Value)
            {
                yield return new TestCaseData(cachedProvider, index);
                index++;
            }
        }
    }

    [TestCaseSource(nameof(ChainSpecRunnerTests))]
    [MaxTime(300000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
    public async Task Smoke((string file, ConfigProvider configProvider) testCase, int testIndex)
    {
        if (testCase.configProvider is null)
        {
            // some weird thing, not worth investigating
            return;
        }

        await SmokeTest(testCase.configProvider, testIndex, 30330);
    }

    [TestCaseSource(nameof(ChainSpecRunnerTests))]
    [MaxTime(30000)] // just to make sure we are not on infinite loop on steps because of incorrect dependencies
    public async Task Smoke_cancel((string file, ConfigProvider configProvider) testCase, int testIndex)
    {
        if (testCase.configProvider is null)
        {
            // some weird thing, not worth investigating
            return;
        }

        await SmokeTest(testCase.configProvider, testIndex, 30430, true);
    }

    [TestCaseSource(nameof(ChainSpecRunnerTests))]
    [MaxTime(300000)]
    public async Task Smoke_CanResolveAllSteps((string file, ConfigProvider configProvider) testCase, int testIndex)
    {
        if (testCase.configProvider is null)
        {
            return;
        }

        PluginLoader pluginLoader = new(
            "plugins",
            new FileSystem(),
            NullLogger.Instance,
            NethermindPlugins.EmbeddedPlugins
        );
        pluginLoader.Load();

        ApiBuilder builder = new ApiBuilder(Substitute.For<IProcessExitSource>(), testCase.configProvider, LimboLogs.Instance);
        IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(testCase.configProvider, builder.ChainSpec);
        EthereumRunner runner = builder.CreateEthereumRunner(plugins);

        INethermindApi api = runner.Api;

        // They normally need the api to be populated by steps, so we mock ouf nethermind api here.
        Build.MockOutNethermindApi((NethermindApi)api);

        api.Config<INetworkConfig>().LocalIp = "127.0.0.1";
        api.Config<INetworkConfig>().ExternalIp = "127.0.0.1";
        var ipResolver = Substitute.For<IIPResolver>();
        ipResolver.ExternalIp.Returns(IPAddress.Parse("127.0.0.1"));
        api.IpResolver = ipResolver;

        api.NodeKey = new InsecureProtectedPrivateKey(TestItem.PrivateKeyA);
        api.FileSystem = Substitute.For<IFileSystem>();
        api.BlockTree = Substitute.For<IBlockTree>();
        api.ReceiptStorage = Substitute.For<IReceiptStorage>();
        api.ReceiptFinder = Substitute.For<IReceiptFinder>();
        api.DbProvider = await TestMemDbProvider.InitAsync();
        api.EthereumEcdsa = new EthereumEcdsa(runner.LifetimeScope.Resolve<ISpecProvider>());

        try
        {
            var stepsLoader = runner.LifetimeScope.Resolve<IEthereumStepsLoader>();
            foreach (var step in stepsLoader.ResolveStepsImplementations())
            {
                runner.LifetimeScope.Resolve(step.StepType);
            }

            // Many components are not part of the step constructor param, so we have resolve them manually here
            foreach (var propertyInfo in api.GetType().Properties())
            {
                // Property with `SkipServiceCollection` make property from container.
                if (propertyInfo.GetCustomAttribute<SkipServiceCollectionAttribute>() is not null)
                {
                    propertyInfo.GetValue(api);
                }

                if (propertyInfo.GetSetMethod() is not null)
                {
                    if (runner.LifetimeScope.ComponentRegistry.TryGetRegistration(new TypedService(propertyInfo.PropertyType), out var registration))
                    {
                        var isFallback = registration.Metadata.ContainsKey(FallbackToFieldFromApi<INethermindApi>.FallbackMetadata);
                        if (!isFallback)
                        {
                            Assert.Fail($"A setter in {nameof(INethermindApi)} of type {propertyInfo.PropertyType} also has a container registration that is not a fallback to api. This is likely a bug.");
                        }
                    }
                }
            }

            if (api.Context.ResolveOptional<IBlockCacheService>() is not null)
            {
                api.Context.Resolve<IBlockCacheService>();
                api.Context.Resolve<InvalidChainTracker>();
                api.Context.Resolve<IBeaconPivot>();
                api.Context.Resolve<BeaconPivot>();
            }
            api.Context.Resolve<IPoSSwitcher>();
            api.Context.Resolve<ISynchronizer>();
            api.Context.Resolve<IAdminEraService>();
            api.Context.Resolve<IRpcModuleProvider>();
            api.Context.Resolve<IMessageSerializationService>();

            // A root registration should not have both keyed and unkeyed registration. This is confusing and may
            // cause unexpected registration. Either have a single non-keyed registration or all keyed-registration,
            // or put them in an unambiguous container class.
            Dictionary<Type, object> keyedTypes = new();
            foreach (var registrations in api.Context.ComponentRegistry.Registrations)
            {
                if (registrations.Lifetime != RootScopeLifetime.Instance) continue;
                foreach (var registrationsService in registrations.Services)
                {
                    if (registrationsService is KeyedService keyedService)
                    {
                        keyedTypes.TryAdd(keyedService.ServiceType, keyedService.ServiceKey);
                    }
                }
            }

            // The following types should not have a global unnamed singleton registration. This is because
            // They are ambiguous by nature. Eg: For `IProtectedPrivateKey`, is it signer key or node key?
            // Consider wrapping them in an type that is clearly global eg: `IWorldStateManager.GlobalWorldState`
            // or using a named registration, or create an explicit child lifetime for that particular instance.
            HashSet<Type> bannedTypeForRootScope =
            [
                typeof(IWorldState),
                typeof(ITransactionProcessor),
                typeof(IVirtualMachine),
                typeof(IDb),
                typeof(IBlockProcessor),
                typeof(IBlockchainProcessor),
                typeof(IProtectedPrivateKey),
                typeof(PublicKey),
                typeof(IPrivateKeyGenerator),
                typeof(string),
            ];

            foreach (var registrations in api.Context.ComponentRegistry.Registrations)
            {
                if (registrations.Lifetime != RootScopeLifetime.Instance) continue;
                foreach (var registrationsService in registrations.Services)
                {
                    if (registrationsService is TypedService typedService)
                    {
                        if (bannedTypeForRootScope.Contains(typedService.ServiceType))
                        {
                            Assert.Fail($"{typedService.ServiceType} has a root registration. This is likely a bug.");
                        }
                        if (keyedTypes.TryGetValue(typedService.ServiceType, out var key))
                        {
                            Assert.Fail($"{typedService.ServiceType} has an unkeyed and keyed ({key}) root registration at the same time. This is likely a bug.");
                        }
                    }
                }
            }
        }
        finally
        {
            await runner.StopAsync();
        }
    }

    private static async Task SmokeTest(ConfigProvider configProvider, int testIndex, int basePort, bool cancel = false)
    {
        // An ugly hack to keep unused types
        Console.WriteLine(typeof(IHiveConfig));

        var tempPath = TempPath.GetTempDirectory();
        Directory.CreateDirectory(tempPath.Path);

        Exception? exception = null;
        try
        {
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            initConfig.BaseDbPath = tempPath.Path;

            IDbConfig dbConfig = configProvider.GetConfig<IDbConfig>();
            dbConfig.FlushOnExit = false;

            INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
            int port = basePort + testIndex;
            networkConfig.P2PPort = port;
            networkConfig.DiscoveryPort = port;

            PluginLoader pluginLoader = new(
                "plugins",
                new FileSystem(),
                NullLogger.Instance,
                NethermindPlugins.EmbeddedPlugins
            );
            pluginLoader.Load();

            ApiBuilder builder = new ApiBuilder(Substitute.For<IProcessExitSource>(), configProvider, LimboLogs.Instance);
            IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(configProvider, builder.ChainSpec);
            EthereumRunner runner = builder.CreateEthereumRunner(plugins);

            using CancellationTokenSource cts = new();

            try
            {
                Task task = runner.Start(cts.Token);
                if (cancel)
                {
                    cts.Cancel();
                }

                await task;
            }
            catch (Exception e)
            {
                exception = e;
            }
            finally
            {
                try
                {
                    await runner.StopAsync();
                }
                catch (Exception e)
                {
                    if (exception is not null)
                    {
                        await TestContext.Error.WriteLineAsync(e.ToString());
                    }
                    else
                    {
                        throw;
                    }
                }
            }
        }
        finally
        {
            try
            {
                tempPath.Dispose();
            }
            catch
            {
                if (exception is not null)
                {
                    // just swallow this exception as otherwise this is recognized as a pattern byt GitHub
                    // await TestContext.Error.WriteLineAsync(e.ToString());
                }
                else
                {
                    throw;
                }
            }
        }
    }
}
