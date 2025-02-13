// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Abstractions;
using System.Runtime.Loader;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus.Clique;
using Nethermind.Core.Test.IO;
using Nethermind.Hive;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Runner.Ethereum;
using Nethermind.Optimism;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Taiko;
using NUnit.Framework;

namespace Nethermind.Runner.Test;

[TestFixture, Parallelizable(ParallelScope.All)]
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

            INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
            int port = basePort + testIndex;
            networkConfig.P2PPort = port;
            networkConfig.DiscoveryPort = port;

            INethermindApi nethermindApi = new ApiBuilder(configProvider, LimboLogs.Instance).Create();
            nethermindApi.RpcModuleProvider = new RpcModuleProvider(new FileSystem(), new JsonRpcConfig(), LimboLogs.Instance);
            EthereumRunner runner = new(nethermindApi);

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
