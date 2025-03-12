// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Abstractions;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.Consensus.Validators;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Hive;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum;
using Nethermind.Optimism;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Serialization.Rlp;
using Nethermind.Taiko;
using Nethermind.UPnP.Plugin;
using NSubstitute;
using NUnit.Framework;

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
            Console.Error.WriteLine($"{configFile}");
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
            typeof(AuRaPlugin),
            typeof(CliquePlugin),
            typeof(OptimismPlugin),
            typeof(TaikoPlugin),
            typeof(EthashPlugin),
            typeof(NethDevPlugin),
            typeof(HivePlugin),
            typeof(UPnPPlugin)
        );
        pluginLoader.Load();

        ApiBuilder builder = new ApiBuilder(Substitute.For<IProcessExitSource>(), testCase.configProvider, LimboLogs.Instance);
        IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(testCase.configProvider, builder.ChainSpec);
        EthereumRunner runner = builder.CreateEthereumRunner(plugins);

        INethermindApi api = runner.Api;
        api.FileSystem = Substitute.For<IFileSystem>();
        api.BlockTree = Substitute.For<IBlockTree>();
        api.ReceiptStorage = Substitute.For<IReceiptStorage>();
        api.BlockValidator = Substitute.For<IBlockValidator>();
        api.DbProvider = Substitute.For<IDbProvider>();

        try
        {
            var stepsLoader = runner.LifetimeScope.Resolve<IEthereumStepsLoader>();
            foreach (var step in stepsLoader.ResolveStepsImplementations())
            {
                runner.LifetimeScope.Resolve(step.StepType);
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
                typeof(AuRaPlugin),
                typeof(CliquePlugin),
                typeof(OptimismPlugin),
                typeof(TaikoPlugin),
                typeof(EthashPlugin),
                typeof(NethDevPlugin),
                typeof(HivePlugin),
                typeof(UPnPPlugin)
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
