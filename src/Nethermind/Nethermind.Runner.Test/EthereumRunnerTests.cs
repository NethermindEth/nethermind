// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Core;
using Autofac.Core.Lifetime;
using Nethermind.Api;
using Nethermind.Api.Extensions;
using Nethermind.Api.Steps;
using Nethermind.Blockchain.Synchronization;
using Nethermind.CensorshipDetector.Plugin;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Validators;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Rewards;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.Era1;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Flashbots;
using Nethermind.HealthChecks;
using Nethermind.Init.Steps;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.GC;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.InvalidChainTracker;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Optimism;
using Nethermind.Runner.Ethereum;
using Nethermind.Runner.Ethereum.Api;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State.OverridableEnv;
using Nethermind.Synchronization;
using Nethermind.Taiko.TaikoSpec;
using Nethermind.TxPool;
using Nethermind.Xdc.Spec;
using NSubstitute;
using NUnit.Framework;
using Testably.Abstractions;
using BlockchainMetrics = Nethermind.Blockchain.Metrics;
using Build = Nethermind.Runner.Test.Ethereum.Build;

namespace Nethermind.Runner.Test;

[TestFixture, Parallelizable(ParallelScope.None)]
public class EthereumRunnerTests
{
    [Test]
    public async Task Startup_pipeline_warmup_waits_for_cleanup_after_cancellation([Values] bool cancelStartup)
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource startup = new();
        Task waiting = StartupWarmupTask.RunAsync(async token =>
        {
            using CancellationTokenRegistration registration = token.Register(() => cancelled.TrySetResult());
            started.SetResult();
            await released.Task;
        }, NullLogger.Instance, cancelStartup ? RunnerTimeout : TimeSpan.FromSeconds(1), startup.Token);
        try
        {
            await started.Task.WaitAsync(RunnerTimeout);
            if (cancelStartup) startup.Cancel();
            await cancelled.Task.WaitAsync(RunnerTimeout);
            Assert.That(waiting.IsCompleted, Is.False, "RPC startup must wait for cleanup.");
            released.SetResult();
            if (cancelStartup)
                Assert.CatchAsync<OperationCanceledException>(() => waiting.WaitAsync(RunnerTimeout));
            else
                await waiting.WaitAsync(RunnerTimeout);
        }
        finally
        {
            released.TrySetResult();
            try { await waiting.WaitAsync(RunnerTimeout); }
            catch (OperationCanceledException) { Assert.That(startup.IsCancellationRequested, Is.True); }
        }
    }

    [Test]
    public async Task Startup_pipeline_warmup_failure_does_not_abort_startup([Values] bool fail)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        await StartupWarmupTask.RunAsync(_ => fail
            ? Task.FromException(new InvalidOperationException("warmup failure"))
            : Task.CompletedTask, new ILogger(logger), RunnerTimeout, CancellationToken.None);

        logger.Received(fail ? 1 : 0).Warn(Arg.Is<string>(message => message.Contains("warmup failure")));
    }

    public enum WarmupScenario { Disabled, Diagnostic, CustomSpec, MissingMerge, CustomPipeline, Supported }

    [TestCase(WarmupScenario.Disabled, "disabled by configuration.")]
    [TestCase(WarmupScenario.Diagnostic, "a database diagnostic mode is enabled.")]
    [TestCase(WarmupScenario.CustomSpec, "the chain uses a custom spec provider.")]
    [TestCase(WarmupScenario.MissingMerge, "the standard Merge plugin is not enabled.")]
    [TestCase(WarmupScenario.CustomPipeline, "the chain uses a custom processing pipeline.")]
    [TestCase(WarmupScenario.Supported, null)]
    public async Task Startup_pipeline_warmup_checks_supported_configuration(WarmupScenario scenario, string? expectedReason)
    {
        ChainSpec spec = LoadWarmupChainSpec();
        InitConfig config = new()
        {
            PipelineWarmupEnabled = scenario != WarmupScenario.Disabled,
            DiagnosticMode = scenario == WarmupScenario.Diagnostic ? DiagnosticMode.MemDb : DiagnosticMode.None
        };
        await using IContainer container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(spec, new ConfigProvider(new InitConfig { DiagnosticMode = DiagnosticMode.MemDb }), NullLogManager.Instance))
            .AddModule(new MergePluginModule())
            .Build();
        INethermindApi api = Substitute.For<INethermindApi>();
        api.Config<IInitConfig>().Returns(config);
        api.SpecProvider.Returns(scenario == WarmupScenario.CustomSpec ? null : new ChainSpecBasedSpecProvider(spec));
        api.Plugins.Returns(scenario == WarmupScenario.MissingMerge ? [] : new INethermindPlugin[] { new MergePlugin(spec, new MergeConfig()) });
        if (scenario == WarmupScenario.Supported) api.MainProcessingContext.Returns(container.Resolve<IMainProcessingContext>());

        Assert.That(StartRpc.GetPipelineWarmupSkipReason(api), Is.EqualTo(expectedReason));
    }

    [TestCase("foundation", false, false)]
    [TestCase("foundation", true, false)]
    [TestCase("hoodi", false, false)]
    [TestCase("hoodi", true, false)]
    [TestCase("sepolia", false, false)]
    [TestCase("sepolia", true, false)]
    [TestCase("foundation", false, true)]
    [TestCase("amsterdam", false, false)]
    [TestCase("amsterdam", true, false)]
    public async Task Startup_pipeline_warmup_processes_payload(string chain, bool flatState, bool authenticated)
    {
        ChainSpec spec = LoadWarmupChainSpec(chain);
        Block originalGenesis = spec.Genesis!;
        Hash256 originalGenesisHash = originalGenesis.Hash!;
        Dictionary<Address, ChainSpecAllocation>? originalAllocations = spec.Allocations;
        using TempPath dataDirectory = TempPath.GetTempDirectory();
        using TempPath secretPath = TempPath.GetTempFile();
        const string secret = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        await File.WriteAllTextAsync(secretPath.Path, secret);
        IRpcAuthentication authentication = authenticated
            ? JwtAuthentication.FromFile(secretPath.Path, Timestamper.Default, NullLogger.Instance)
            : NoAuthentication.Instance;
        using CancellationTokenSource cancellation = new(RunnerTimeout);
        // Holding the node's RPC and engine ports fails the warmup if it binds them, where the consensus client could reach it.
        using System.Net.Sockets.TcpListener livePorts = new(IPAddress.Loopback, 0);
        livePorts.Start();
        int livePort = ((IPEndPoint)livePorts.LocalEndpoint).Port;
        ConfigProvider liveConfig = new(new InitConfig { BaseDbPath = dataDirectory.Path },
            new JsonRpcConfig
            {
                Host = "127.0.0.1", Port = livePort, EnginePort = livePort, EnabledModules = [ModuleType.Eth], JwtSecretFile = secretPath.Path,
                RequestQueueLimit = 7, MaxConcurrentSharedRequests = 11
            });
        ThreadPool.GetMinThreads(out int minWorkerThreads, out int minCompletionPortThreads);

        await StartupPipelineWarmer.WarmupAsync(spec, liveConfig, flatState, cancellation.Token, authentication);

        ThreadPool.GetMinThreads(out int warmedWorkerThreads, out int warmedCompletionPortThreads);
        using (Assert.EnterMultipleScope())
        {
            Assert.That((warmedWorkerThreads, warmedCompletionPortThreads), Is.EqualTo((minWorkerThreads, minCompletionPortThreads)));
            AssertRpcLimit(RpcLimits.Default.AcquireQueuedSlot, RpcLimits.Default.DecrementQueuedCalls, 7);
            AssertRpcLimit(RpcLimits.Default.AcquireSharedSlot, RpcLimits.Default.DecrementSharedCalls, 11);
            Assert.That(spec.Genesis, Is.SameAs(originalGenesis));
            Assert.That(originalGenesis.Hash, Is.EqualTo(originalGenesisHash));
            Assert.That(spec.Allocations, Is.SameAs(originalAllocations));
            Assert.That(File.ReadAllText(secretPath.Path), Is.EqualTo(secret));
            Assert.That(Directory.EnumerateDirectories(Path.Combine(dataDirectory.Path, "startup-warmup")), Is.Empty);
        }
    }

    [Test]
    public async Task Startup_pipeline_warmup_keeps_live_head_metrics()
    {
        using BasicTestBlockchain live = await BasicTestBlockchain.Create();
        // Live heights above the warm chain's make a warm value left in a gauge visible.
        for (int i = 0; i < 4; i++) await live.AddBlock();
        using TempPath dataDirectory = TempPath.GetTempDirectory();
        using CancellationTokenSource cancellation = new(RunnerTimeout);

        // The live chain advances before the warm blocks publish, as a restarted node does while it replays to its head.
        await StartupPipelineWarmer.WarmupAsync(LoadWarmupChainSpec(), WarmupConfig(dataDirectory.Path), false, cancellation.Token,
            configureContainer: OnWarmRpcStart(() => live.AddBlock().GetAwaiter().GetResult()), liveBlockTree: live.BlockTree);

        ulong liveHead = live.BlockTree.Head!.Number;
        Assert.That((BlockchainMetrics.Blocks, BlockchainMetrics.BlockchainHeight, BlockchainMetrics.BestKnownBlockNumber),
            Is.EqualTo((liveHead, liveHead, live.BlockTree.BestKnownNumber)));
    }

    [Test]
    public async Task Startup_pipeline_warmup_restores_live_metrics_after_warm_reports()
    {
        using BasicTestBlockchain live = await BasicTestBlockchain.Create();
        for (int i = 0; i < 4; i++) await live.AddBlock();
        using ManualResetEventSlim reporting = new();
        using ManualResetEventSlim release = new();
        // Holds the warm report on its thread-pool thread, before it publishes the height gauges.
        InterfaceLogger slowBlockLogger = Substitute.For<InterfaceLogger>();
        slowBlockLogger.IsWarn.Returns(true);
        slowBlockLogger.When(l => l.Warn(Arg.Any<string>())).Do(_ =>
        {
            reporting.Set();
            release.Wait();
        });
        ILogger slowBlocks = new(slowBlockLogger);
        ILogManager logManager = Substitute.For<ILogManager>();
        logManager.GetLogger("SlowBlocks").Returns(slowBlocks);
        StartupPipelineWarmer.WarmMetrics warmMetrics = new();
        IProcessingStats stats = new StartupPipelineWarmer.WarmProcessingStats(live.StateReader, logManager, new BlocksConfig { SlowBlockThresholdMs = 0 }, warmMetrics);

        stats.UpdateStats([Nethermind.Core.Test.Builders.Build.A.Block.WithNumber(2).TestObject], null, 1_000);
        Assert.That(reporting.Wait(RunnerTimeout), Is.True);
        Task restore = warmMetrics.RestoreLiveAsync(live.BlockTree);
        bool restoredBeforeReport = restore.IsCompleted;
        release.Set();
        await restore.WaitAsync(RunnerTimeout);
        // The warm report has published by now, so a restore that ran before it would have been overwritten.
        Assert.That(SpinWait.SpinUntil(() => Volatile.Read(ref warmMetrics.PendingReports) == 0, RunnerTimeout), Is.True);

        ulong liveHead = live.BlockTree.Head!.Number;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(restoredBeforeReport, Is.False);
            Assert.That((BlockchainMetrics.Blocks, BlockchainMetrics.BlockchainHeight), Is.EqualTo((liveHead, liveHead)));
        }
    }

    [TestCase(2UL, new ulong[] { 10 }, 0UL, 10UL, TestName = "Warm metric value is replaced by the live value")]
    [TestCase(9UL, new ulong[] { 10 }, 0UL, 9UL, TestName = "Live metric value published after the warm one is kept")]
    [TestCase(2UL, new ulong[] { 10, 11 }, 0UL, 11UL, TestName = "Live head moving during the metric swap is followed")]
    [TestCase(2UL, new ulong[] { 10 }, 12UL, 12UL, TestName = "Live metric value published during the swap is kept")]
    public void Startup_pipeline_warmup_replaces_only_warm_metric_values(ulong gauge, ulong[] liveValues, ulong publishedOnFirstRead, ulong expected)
    {
        const ulong warmValue = 2;
        ulong[] metric = [gauge];
        int reads = 0;

        StartupPipelineWarmer.ReplaceWarmValue(ref metric[0], warmValue, () =>
        {
            if (reads == 0 && publishedOnFirstRead != 0) metric[0] = publishedOnFirstRead;
            return liveValues[Math.Min(reads++, liveValues.Length - 1)];
        });

        Assert.That(metric[0], Is.EqualTo(expected));
    }

    private static void AssertRpcLimit(Action acquire, Action release, int limit)
    {
        int acquired = 0;
        try
        {
            for (; acquired < limit; acquired++) acquire();
            Assert.Throws<LimitExceededException>(() =>
            {
                acquire();
                acquired++;
            });
        }
        finally
        {
            for (int i = 0; i < acquired; i++) release();
        }
    }

    [Test]
    public void Startup_pipeline_warmup_cancellation_does_not_create_storage()
    {
        using TempPath dataDirectory = TempPath.GetTempDirectory();

        Assert.ThrowsAsync<OperationCanceledException>(() => StartupPipelineWarmer.WarmupAsync(new ChainSpec(),
            WarmupConfig(dataDirectory.Path), false, new CancellationToken(canceled: true)));

        Assert.That(Directory.Exists(dataDirectory.Path), Is.False);
    }

    [Test]
    public void Startup_pipeline_warmup_cleans_up_after_rpc_start_cancellation([Values] bool flatState)
    {
        ChainSpec spec = LoadWarmupChainSpec();
        using TempPath dataDirectory = TempPath.GetTempDirectory();
        using CancellationTokenSource cancellation = new(RunnerTimeout);

        Assert.CatchAsync<OperationCanceledException>(() => StartupPipelineWarmer.WarmupAsync(spec,
            WarmupConfig(dataDirectory.Path), flatState, cancellation.Token, configureContainer: CancelWhenRpcStarts(cancellation)));

        Assert.That(Directory.EnumerateDirectories(Path.Combine(dataDirectory.Path, "startup-warmup")), Is.Empty);
    }

    private static IConfigProvider WarmupConfig(string dataDirectory) => new ConfigProvider(new InitConfig { BaseDbPath = dataDirectory });

    private static Action<ContainerBuilder> CancelWhenRpcStarts(CancellationTokenSource cancellation, Action? beforeCancel = null) =>
        OnWarmRpcStart(() =>
        {
            beforeCancel?.Invoke();
            cancellation.Cancel();
        });

    private static Action<ContainerBuilder> OnWarmRpcStart(Action action)
    {
        IJsonRpcServiceConfigurer configurer = Substitute.For<IJsonRpcServiceConfigurer>();
        configurer.When(c => c.Configure(Arg.Any<Microsoft.Extensions.DependencyInjection.IServiceCollection>())).Do(_ => action());
        return builder => builder.AddSingleton(configurer);
    }

    private static ChainSpec LoadWarmupChainSpec(string chain = "foundation")
    {
        if (chain == "amsterdam")
        {
            using Stream source = typeof(IConfig).Assembly.GetManifestResourceStream("Nethermind.Config.chainspec.hoodi.json")!;
            JsonNode genesis = JsonNode.Parse(source)!;
            genesis["config"]!["amsterdamTime"] = 0;
            using MemoryStream modified = new(System.Text.Encoding.UTF8.GetBytes(genesis.ToJsonString()));
            return new AutoDetectingChainSpecLoader(new EthereumJsonSerializer(), NullLogManager.Instance).Load(modified);
        }
        ChainSpecFileLoader loader = new(new EthereumJsonSerializer(), NullLogManager.Instance);
        return loader.LoadEmbeddedOrFromFile($"chainspec/{chain}.json");
    }

    [Test, Platform("Win")]
    public void Startup_pipeline_warmup_cleanup_failure_preserves_cancellation()
    {
        using TempPath dataDirectory = TempPath.GetTempDirectory();
        using CancellationTokenSource cancellation = new(RunnerTimeout);
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        FileStream? lockedFile = null;
        try
        {
            Assert.CatchAsync<OperationCanceledException>(() => StartupPipelineWarmer.WarmupAsync(LoadWarmupChainSpec(),
                WarmupConfig(dataDirectory.Path), false, cancellation.Token, logger: new ILogger(logger),
                configureContainer: CancelWhenRpcStarts(cancellation, () =>
                {
                    string directory = Directory.GetDirectories(Path.Combine(dataDirectory.Path, "startup-warmup"))[0];
                    lockedFile = new FileStream(Path.Combine(directory, "locked"), FileMode.Create, FileAccess.Write, FileShare.None);
                })));
            logger.Received(1).Warn(Arg.Is<string>(message => message.Contains("Could not delete startup warmup directory")));
        }
        finally
        {
            lockedFile?.Dispose();
        }
    }

    [Test]
    public async Task Startup_pipeline_warmup_does_not_control_instruction_warmup([Values] bool pipelineWarmupEnabled, [Values] bool evmWarmupEnabled)
    {
        IOverridableEnvFactory envFactory = Substitute.For<IOverridableEnvFactory>();
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<IInitConfig>(new InitConfig { PipelineWarmupEnabled = pipelineWarmupEnabled, EvmWarmupEnabled = evmWarmupEnabled })
            .AddSingleton(envFactory)
            .AddSingleton<EvmWarmer>()
            .Build();

        await container.Resolve<EvmWarmer>().Execute(CancellationToken.None);

        envFactory.Received(evmWarmupEnabled ? 1 : 0).Create();
    }

    static EthereumRunnerTests()
    {
        // Trigger plugins loading early to ensure TypeDiscovery caches plugin's types
        PluginLoader pluginLoader = new("plugins", new RealFileSystem(), NullLogger.Instance, NethermindPlugins.EmbeddedPlugins);
        pluginLoader.Load();

        AssemblyLoadContext.Default.Resolving += static (_, _) => null;
    }

    /// <summary>Budget for a single start or stop of a runner under test.</summary>
    /// <remarks>This is what fails a smoke case whose steps deadlock on incorrect dependencies. A test-level
    /// <see cref="MaxTimeAttribute"/> would only report once the test returns, and it also counts setup, which
    /// is unbounded and can stall on slow CI hosts. Without a bound, a start or stop that never completes takes the
    /// whole assembly into the CI hang-dump watchdog. A runner on an in-memory DB is orders of magnitude under this.</remarks>
    private static readonly TimeSpan RunnerTimeout = TimeSpan.FromSeconds(30);

    private static readonly Lazy<ICollection<(string file, ConfigProvider configProvider)>>? _cachedProviders = new(InitOnce);

    private static ICollection<(string file, ConfigProvider configProvider)> InitOnce()
    {
        // we need this to discover ChainSpecEngineParameters
        _ = new[] { typeof(CliqueChainSpecEngineParameters), typeof(OptimismChainSpecEngineParameters), typeof(TaikoChainSpecEngineParameters), typeof(XdcChainSpecEngineParameters) };

        // by pre-caching configs providers we make the tests do lot less work
        ConcurrentQueue<(string, ConfigProvider)> resultQueue = new();
        Parallel.ForEach(Directory.GetFiles("configs"), configFile =>
        {
            ConfigProvider configProvider = new();
            configProvider.AddSource(new JsonConfigSource(configFile));
            configProvider.Initialize();
            resultQueue.Enqueue((configFile, configProvider));
        });

        // Sort so that is is consistent so that its easy to run via Rider.
        List<(string, ConfigProvider)> result = [.. resultQueue];
        result.Sort();

        {
            // Special case for verify trie on state sync finished
            ConfigProvider configProvider = new();
            configProvider.AddSource(new JsonConfigSource("configs/mainnet.json"));
            configProvider.Initialize();
            configProvider.GetConfig<ISyncConfig>().VerifyTrieOnStateSyncFinished = true;
            result.Add(("mainnet-verify-trie-starter", configProvider));
        }

        {
            // Flashbots
            ConfigProvider configProvider = new();
            configProvider.AddSource(new JsonConfigSource("configs/mainnet.json"));
            configProvider.Initialize();
            configProvider.GetConfig<IFlashbotsConfig>().Enabled = true;
            result.Add(("flashbots", configProvider));
        }

        {
            // Censorship detector
            ConfigProvider configProvider = new();
            configProvider.AddSource(new JsonConfigSource("configs/mainnet.json"));
            configProvider.Initialize();
            configProvider.GetConfig<ICensorshipDetectorConfig>().Enabled = true;
            result.Add(("censorship-detector", configProvider));
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
            foreach ((string file, ConfigProvider configProvider) in _cachedProviders!.Value)
            {
                yield return new TestCaseData((Path.GetFileName(file), configProvider), index);
                index++;
            }
        }
    }

    [TestCaseSource(nameof(ChainSpecRunnerTests))]
    public async Task Smoke((string file, ConfigProvider configProvider) testCase, int testIndex)
    {
        if (testCase.configProvider is null)
        {
            // some weird thing, not worth investigating
            return;
        }

        if (testCase.file.Contains("none.json")) Assert.Ignore("engine port missing");
        if (testCase.file.Contains("radius_testnet-sepolia.json")) Assert.Ignore("sequencer url not specified");

        await SmokeTest(testCase.configProvider, testIndex, 30330);
    }

    [TestCaseSource(nameof(ChainSpecRunnerTests))]
    public async Task Smoke_cancel((string file, ConfigProvider configProvider) testCase, int testIndex)
    {
        if (testCase.configProvider is null)
        {
            // some weird thing, not worth investigating
            return;
        }

        if (testCase.file.Contains("none.json")) Assert.Ignore("engine port missing");

        await SmokeTest(testCase.configProvider, testIndex, 30430, true);
    }

    /// <summary>
    /// Proves the real production container resolves a command by name and that a command which cannot do its
    /// job fails the run instead of exiting Ok. Under <see cref="DiagnosticMode.MemDb"/> the block tree has no
    /// head, so <c>verify-trie</c> has nothing to verify. Closure contents are covered by
    /// <c>EthereumStepsManagerTests</c>.
    /// </summary>
    [Test]
    [MaxTime(60000)]
    public async Task Command_run_that_cannot_do_its_job_fails_without_exiting_ok()
    {
        Rlp.ResetDecoders(); // The global decoder registry is shared with every other test in this assembly.

        ConfigProvider configProvider = new();
        configProvider.AddSource(new JsonConfigSource("configs/mainnet.json"));
        configProvider.Initialize();

        PluginLoader pluginLoader = new("plugins", new RealFileSystem(), NullLogger.Instance, NethermindPlugins.EmbeddedPlugins);
        pluginLoader.Load();

        IProcessExitSource processExitSource = Substitute.For<IProcessExitSource>();
        ApiBuilder builder = new(processExitSource, configProvider, LimboLogs.Instance);
        IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(configProvider, builder.ChainSpec);
        plugins.Add(new RunnerTestPlugin(true));
        EthereumRunner runner = builder.CreateEthereumRunner(plugins, command: "verify-trie");

        try
        {
            Assert.That(async () => await runner.Start(CancellationToken.None).WaitAsync(RunnerTimeout),
                Throws.TypeOf<StepDependencyException>());

            processExitSource.DidNotReceive().Exit(ExitCodes.Ok);
        }
        finally
        {
            await runner.StopAsync();
        }
    }

    [TestCaseSource(nameof(ChainSpecRunnerTests))]
    public async Task Smoke_CanResolveAllSteps((string file, ConfigProvider configProvider) testCase, int testIndex)
    {
        if (testCase.configProvider is null)
        {
            return;
        }

        PluginLoader pluginLoader = new(
            "plugins",
            new RealFileSystem(),
            NullLogger.Instance,
            NethermindPlugins.EmbeddedPlugins
        );
        pluginLoader.Load();

        ApiBuilder builder = new(Substitute.For<IProcessExitSource>(), testCase.configProvider, LimboLogs.Instance);
        IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(testCase.configProvider, builder.ChainSpec);
        plugins.Add(new RunnerTestPlugin(true));
        EthereumRunner runner = builder.CreateEthereumRunner(plugins, command: null);

        INethermindApi api = runner.Api;

        // They normally need the api to be populated by steps, so we mock out nethermind api here.
        Build.MockOutNethermindApi((NethermindApi)api);

        api.Config<INetworkConfig>().LocalIp = "127.0.0.1";
        api.Config<INetworkConfig>().ExternalIp = "127.0.0.1";
        _ = api.Config<IHealthChecksConfig>(); // Randomly fail type discovery if not resolved early.

        api.BlockProducerRunner = Substitute.For<IBlockProducerRunner>();

        try
        {
            IEthereumStepsLoader stepsLoader = runner.LifetimeScope.Resolve<IEthereumStepsLoader>();
            foreach (StepInfo step in stepsLoader.ResolveStepsImplementations())
            {
                runner.LifetimeScope.Resolve(step.StepType);
            }

            // Many components are not part of the step constructor param, so we have resolve them manually here
            foreach (PropertyInfo? propertyInfo in api.GetType().GetProperties())
            {
                // Property with `SkipServiceCollection` make property from container.
                if (propertyInfo.GetCustomAttribute<SkipServiceCollectionAttribute>() is not null)
                {
                    propertyInfo.GetValue(api);
                }

                if (propertyInfo.GetSetMethod() is not null)
                {
                    if (runner.LifetimeScope.ComponentRegistry.TryGetRegistration(new TypedService(propertyInfo.PropertyType), out IComponentRegistration? registration))
                    {
                        bool isFallback = registration.Metadata.ContainsKey(FallbackToFieldFromApi<INethermindApi>.FallbackMetadata);
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
            if (api.Context.IsRegistered<NoSyncGcRegionStrategy>())
            {
                Assert.That(api.Context.Resolve<IGCStrategy>(), Is.TypeOf(api.Config<IInitConfig>().DisableGcOnNewPayload
                    ? typeof(NoSyncGcRegionStrategy)
                    : typeof(NoGCStrategy)));
            }
            api.Context.Resolve<IPoSSwitcher>();
            api.Context.Resolve<ISynchronizer>();
            api.Context.Resolve<IAdminEraService>();
            api.Context.Resolve<IRpcModuleProvider>();
            api.Context.Resolve<IMessageSerializationService>();
            api.Context.Resolve<ISealValidator>();
            api.Context.Resolve<ISealer>();
            api.Context.Resolve<ISealEngine>();
            api.Context.Resolve<IRewardCalculatorSource>();
            api.Context.Resolve<IBlockValidator>();
            api.Context.Resolve<IHeaderValidator>();
            api.Context.Resolve<IUnclesValidator>();
            api.Context.Resolve<ITxValidator>();
            api.Context.Resolve<IReadOnlyTxProcessingEnvFactory>();
            api.Context.Resolve<IBlockProducerEnvFactory>().CreatePersistent();

            // A root registration should not have both keyed and unkeyed registration. This is confusing and may
            // cause unexpected registration. Either have a single non-keyed registration or all keyed-registration,
            // or put them in an unambiguous container class.
            Dictionary<Type, object> keyedTypes = [];
            foreach (IComponentRegistration registrations in api.Context.ComponentRegistry.Registrations)
            {
                if (registrations.Lifetime != RootScopeLifetime.Instance) continue;
                foreach (Service registrationsService in registrations.Services)
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
                typeof(INetworkStorage),
                typeof(NetworkStorage),
                typeof(ITracer), // Completely different construction on every case
                typeof(IReadOnlyStateProvider), // For which block? Use IChainHeadInfoProvider, or preferably, IStateReader instead
                typeof(string),
            ];

            foreach (IComponentRegistration registrations in api.Context.ComponentRegistry.Registrations)
            {
                if (registrations.Lifetime != RootScopeLifetime.Instance) continue;
                foreach (Service registrationsService in registrations.Services)
                {
                    if (registrationsService is TypedService typedService)
                    {
                        if (bannedTypeForRootScope.Contains(typedService.ServiceType))
                        {
                            Assert.Fail($"{typedService.ServiceType} has a root registration. This is likely a bug.");
                        }
                        if (keyedTypes.TryGetValue(typedService.ServiceType, out object? key))
                        {
                            Assert.Fail($"{typedService.ServiceType} has an unkeyed and keyed ({key}) root registration at the same time. This is likely a bug.");
                        }
                    }
                }
            }
        }
        finally
        {
            await runner.StopAsync().WaitAsync(RunnerTimeout);
        }
    }

    private static async Task SmokeTest(ConfigProvider configProvider, int testIndex, int basePort, bool cancel = false)
    {
        Stopwatch phaseTimer = Stopwatch.StartNew();
        Rlp.ResetDecoders(); // One day this will be fix. But that day is not today, because it is seriously difficult.
        configProvider.GetConfig<IInitConfig>().DiagnosticMode = DiagnosticMode.MemDb;
        TempPath tempPath = TempPath.GetTempDirectory();
        Directory.CreateDirectory(tempPath.Path);

        Exception? exception = null;
        try
        {
            IInitConfig initConfig = configProvider.GetConfig<IInitConfig>();
            initConfig.BaseDbPath = tempPath.Path;

            IDbConfig dbConfig = configProvider.GetConfig<IDbConfig>();
            dbConfig.FlushOnExit = FlushOnExitMode.None;

            INetworkConfig networkConfig = configProvider.GetConfig<INetworkConfig>();
            int port = basePort + testIndex;
            networkConfig.P2PPort = port;
            networkConfig.DiscoveryPort = port;

            PluginLoader pluginLoader = new(
                "plugins",
                new RealFileSystem(),
                NullLogger.Instance,
                NethermindPlugins.EmbeddedPlugins
            );
            pluginLoader.Load();

            ApiBuilder builder = new(Substitute.For<IProcessExitSource>(), configProvider, LimboLogs.Instance);
            IList<INethermindPlugin> plugins = await pluginLoader.LoadPlugins(configProvider, builder.ChainSpec);
            plugins.Add(new RunnerTestPlugin());
            EthereumRunner runner = builder.CreateEthereumRunner(plugins, command: null);
            LogPhase("setup", phaseTimer);

            using CancellationTokenSource cts = new();

            try
            {
                Task task = runner.Start(cts.Token);
                if (cancel)
                {
                    cts.Cancel();
                }

                await task.WaitAsync(RunnerTimeout);
            }
            finally
            {
                LogPhase("start", phaseTimer);
                try
                {
                    await runner.StopAsync().WaitAsync(RunnerTimeout);
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
                finally
                {
                    LogPhase("stop", phaseTimer);
                }
            }
        }
        finally
        {
            try
            {
                tempPath.Dispose();
            }
            catch (Exception disposeException)
            {
                if (disposeException is not null)
                {
                    // just swallow this exception as otherwise this is recognized as a pattern byt GitHub
                    // await TestContext.Error.WriteLineAsync(e.ToString());
                }
                else
                {
                    throw;
                }
            }

            LogPhase("teardown", phaseTimer);
        }
    }

    /// <summary>Reports how long a smoke test phase took, so a slow case names the phase that stalled.</summary>
    private static void LogPhase(string name, Stopwatch phaseTimer)
    {
        TestContext.Out.WriteLine($"{name}: {phaseTimer.ElapsedMilliseconds} ms");
        phaseTimer.Restart();
    }

    private class RunnerTestPlugin(bool forStepTest = false) : INethermindPlugin
    {
        public string Name { get; } = "Runner test plugin";
        public string Description { get; } = "A plugin to pass runner test and make it faster";
        public string Author { get; } = "";
        public bool Enabled { get; } = true;

        public IModule Module => new RunnerTestModule(forStepTest);

        private class RunnerTestModule(bool forStepTest) : Autofac.Module
        {
            protected override void Load(ContainerBuilder builder)
            {
                base.Load(builder);

                IIPResolver ipResolver = Substitute.For<IIPResolver>();
                ipResolver.Resolve(Arg.Any<CancellationToken>())
                    .Returns(new ValueTask<IIPResolver.NethermindIp>(new IIPResolver.NethermindIp(IPAddress.Parse("127.0.0.1"), IPAddress.Parse("127.0.0.1"))));

                builder
                    .AddSingleton(ipResolver)
                    .AddDecorator<IInitConfig>((ctx, initConfig) =>
                    {
                        initConfig.DiagnosticMode = DiagnosticMode.MemDb;
                        initConfig.InRunnerTest = true;
                        return initConfig;
                    })
                    .AddDecorator<IJsonRpcConfig>((ctx, jsonRpcConfig) =>
                    {
                        jsonRpcConfig.PreloadRpcModules = true; // So that rpc is resolved early so that we know if something is wrong in test
                        return jsonRpcConfig;
                    });

                if (forStepTest)
                {
                    // Special case for aura where by default it try to cast the main blockchain processor
                    // to extract the reporting validator. The blockchain processor is not DI so it fail in step test but
                    // pass in runner test.
                    builder
                        .AddSingleton(Substitute.For<IReportingValidator>())
                        // Pin a deterministic node key so resolving components does not load/generate a real key file.
                        .AddKeyedSingleton<IProtectedPrivateKey>(IProtectedPrivateKey.NodeKey, new InsecureProtectedPrivateKey(TestItem.PrivateKeyA))
                        ;
                }

            }
        }
    }
}
