// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Net;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.EthStats;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum;
using Nethermind.Db.Blooms;
using Nethermind.Init.Steps;
using Nethermind.Network;
using Nethermind.Network.Discovery;
using Nethermind.Network.Rlpx;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.State;
using Nethermind.Synchronization;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
public class EthereumRunnerTests
{
    static EthereumRunnerTests()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            return null;
        };
    }

    private static readonly Lazy<ICollection>? _cachedProviders = new(InitOnce);

    private static ICollection InitOnce()
    {
        // by pre-caching configs providers we make the tests do lot less work
        ConcurrentQueue<(string, ConfigProvider)> result = new();
        Parallel.ForEach(Directory.GetFiles("configs"), configFile =>
        {
            var configProvider = new ConfigProvider();
            configProvider.AddSource(new JsonConfigSource(configFile));
            configProvider.Initialize();
            result.Enqueue((configFile, configProvider));
        });

        return result;
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
    public void BuildTest((string file, ConfigProvider configProvider) testCase, int _)
    {
        if (testCase.configProvider is null)
        {
            // some weird thing, not worth investigating
            return;
        }

        using IContainer container = new ApiBuilder(testCase.configProvider, Substitute.For<IProcessExitSource>(), LimboLogs.Instance)
            .Create(Array.Empty<Type>());

        {
            // Ideally, we don't have any of there blocks.
            INethermindApi nethermindApi = container.Resolve<INethermindApi>();
            nethermindApi.BlockTree = Build.A.BlockTree().OfChainLength(1).TestObject;
            nethermindApi.StateReader = Substitute.For<IStateReader>();
            nethermindApi.NodeStorageFactory = new NodeStorageFactory(INodeStorage.KeyScheme.Current, LimboLogs.Instance);
            nethermindApi.DbProvider = TestMemDbProvider.Init();
            nethermindApi.ReceiptStorage = Substitute.For<IReceiptStorage>();
            nethermindApi.ReceiptFinder = Substitute.For<IReceiptFinder>();
            nethermindApi.BlockValidator = Substitute.For<IBlockValidator>();
            nethermindApi.NodeKey = new ProtectedPrivateKey(TestItem.PrivateKeyA, Path.GetTempPath());
            nethermindApi.EthereumEcdsa = new EthereumEcdsa(0);
            nethermindApi.BackgroundTaskScheduler = Substitute.For<IBackgroundTaskScheduler>();
            nethermindApi.TxPool = Substitute.For<ITxPool>();
            nethermindApi.TrieStore = Substitute.For<ITrieStore>();

            IIPResolver ipResolver = Substitute.For<IIPResolver>();
            ipResolver.ExternalIp.Returns(IPAddress.Loopback);
            nethermindApi.IpResolver = ipResolver;

            INetworkConfig networkConfig = container.Resolve<INetworkConfig>();
            networkConfig.ExternalIp = "127.0.0.1";
            networkConfig.LocalIp = "127.0.0.1";
        }

        container.Resolve<EthereumRunner>();
        foreach (StepInfo loadStep in container.Resolve<IEthereumStepsLoader>().LoadSteps())
        {
            container.Resolve(loadStep.StepType);
        }

        // TODO: Ideally, these should not be here and declared in the constructor of the steps. But until we stop
        // resolving things manually because of steps manually updating api, we can't really do much.
        container.Resolve<ISynchronizer>();
        container.Resolve<SyncedTxGossipPolicy>();
        container.Resolve<ISyncServer>();
        container.Resolve<IDiscoveryApp>();
        container.Resolve<IPeerManager>();
        container.Resolve<ISessionMonitor>();
        container.Resolve<IRlpxHost>();
        container.Resolve<IStaticNodesManager>();
        container.Resolve<Func<NodeSourceToDiscV4Feeder>>();
        container.Resolve<IProtocolsManager>();
        container.Resolve<SnapCapabilitySwitcher>();
        container.Resolve<ISyncPeerPool>();
    }

    private static async Task SmokeTest(ConfigProvider configProvider, int testIndex, int basePort, bool cancel = false)
    {
        Type type1 = typeof(ITxPoolConfig);
        Type type2 = typeof(INetworkConfig);
        Type type3 = typeof(IKeyStoreConfig);
        Type type4 = typeof(IDbConfig);
        Type type7 = typeof(IEthStatsConfig);
        Type type8 = typeof(ISyncConfig);
        Type type9 = typeof(IBloomConfig);

        Console.WriteLine(type1.Name);
        Console.WriteLine(type2.Name);
        Console.WriteLine(type3.Name);
        Console.WriteLine(type4.Name);
        Console.WriteLine(type7.Name);
        Console.WriteLine(type8.Name);
        Console.WriteLine(type9.Name);

        var tempPath = TempPath.GetTempDirectory();
        Directory.CreateDirectory(tempPath.Path);

        Exception? exception = null;
        try
        {
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            initConfig.BaseDbPath = tempPath.Path;

            INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
            int port = basePort + testIndex;
            networkConfig.P2PPort = port;
            networkConfig.DiscoveryPort = port;

            IContainer container = new ApiBuilder(configProvider, Substitute.For<IProcessExitSource>(), LimboLogs.Instance).Create(Array.Empty<Type>());
            container.Resolve<INethermindApi>().RpcModuleProvider = new RpcModuleProvider(new FileSystem(), new JsonRpcConfig(), LimboLogs.Instance);
            EthereumRunner runner = container.Resolve<EthereumRunner>();

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
