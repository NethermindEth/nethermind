// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Authentication;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Memory;
using Nethermind.Core.ServiceStopper;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Db.Rocks.Config;
using Nethermind.HealthChecks;
using Nethermind.Int256;
using Nethermind.Init.Steps;
using Nethermind.Runner.Ethereum.Modules;
using Nethermind.Runner.Ethereum.Steps;
using Nethermind.Consensus.Ethash;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.KeyStore.Config;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Network.Config;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.TxPool;
using BlockchainMetrics = Nethermind.Blockchain.Metrics;

namespace Nethermind.Runner.Ethereum;

internal static class StartupPipelineWarmer
{
    private static readonly Address StorageContract = new("0x0000000000000000000000000000000000001000");
    private static readonly Address RevertContract = new("0x0000000000000000000000000000000000001001");
    private const int SenderCount = 8;
    // Exceeds the 64-item thresholds for parallel trie roots and background bloom computation.
    private const int TransactionCount = 128;

    /// <summary>Exercises an Ethereum payload and a contract call using disposable storage.</summary>
    /// <remarks>
    /// Uses production component types so any tiered compilation observes RocksDB rather than a substitute database.
    /// The workload is deliberately bounded; it does not force a JIT tier or prevent runtime promotion.
    /// </remarks>
    internal static async Task WarmupAsync(ChainSpec source, IConfigProvider liveConfig, bool flatState, CancellationToken cancellationToken,
        IRpcAuthentication? authentication = null, ILogger logger = default, Action<ContainerBuilder>? configureContainer = null,
        IBlockTree? liveBlockTree = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DirectoryInfo directory = Directory.CreateDirectory(Path.Combine(liveConfig.GetConfig<IInitConfig>().BaseDbPath, "startup-warmup", Guid.NewGuid().ToString("N")));
        // One key per sender, then the EIP-7702 authority.
        PrivateKey[] keys = new PrivateKey[SenderCount + 1];
        for (int i = 0; i < keys.Length; i++) keys[i] = new PrivateKey((i + 1).ToString("x64"));
        ThreadPool.GetMinThreads(out int minWorkerThreads, out int minCompletionPortThreads);
        WarmMetrics warmMetrics = new();
        try
        {
            IRpcAuthentication warmAuthentication = authentication is JwtAuthentication
                ? JwtAuthentication.FromSecret(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), Timestamper.Default, logger)
                : NoAuthentication.Instance;
            string? token = (warmAuthentication as JwtAuthentication)?.CreateWarmupToken();
            await RunAsync(source, liveConfig, flatState, directory.FullName, token, warmAuthentication, keys, configureContainer, warmMetrics, cancellationToken);
        }
        finally
        {
            // The warm node's RegisterRpcModules step raises the process-wide minimums again.
            ThreadPool.SetMinThreads(minWorkerThreads, minCompletionPortThreads);
            if (liveBlockTree is not null) await warmMetrics.RestoreLiveAsync(liveBlockTree);
            foreach (PrivateKey key in keys) key.Dispose();
            try
            {
                directory.Delete(recursive: true);
            }
            catch (Exception exception)
            {
                if (logger.IsWarn) logger.Warn($"Could not delete startup warmup directory '{directory.FullName}': {exception.Message}");
            }
        }
    }

    private static async Task RunAsync(ChainSpec source, IConfigProvider liveConfig, bool flatState, string directory, string? token, IRpcAuthentication authentication,
        PrivateKey[] keys, Action<ContainerBuilder>? configureContainer, WarmMetrics warmMetrics, CancellationToken cancellationToken)
    {
        ulong timestamp = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ChainSpec chainSpec = CreateChainSpec(source, liveConfig.GetConfig<IBlocksConfig>().MinGasPrice, keys);
        ConfigProvider config = CreateConfig(liveConfig, flatState, directory, token is not null, keys[0]);
        // A separate root prevents a fallback registration from reaching the live database or world state.
        ProcessExitSource exitSource = new(cancellationToken);
        ContainerBuilder builder = new ContainerBuilder()
            .AddModule(new NethermindRunnerModule(new EthereumJsonSerializer(), chainSpec, config, exitSource,
                [new EthashPlugin(chainSpec, config.GetConfig<IMiningConfig>()), new MergePlugin(chainSpec, config.GetConfig<IMergeConfig>()), new HealthChecksPlugin()],
                // Enables every log level without output, so message construction is compiled too.
                null, LimboLogs.Instance))
            .AddSingleton<ISpecProvider>(new ChainSpecBasedSpecProvider(source))
            .AddSingleton(warmMetrics)
            .AddScoped<IProcessingStats, WarmProcessingStats>();
        // A disposable secret exercises production authentication without granting access to the live Engine API.
        builder.AddSingleton(authentication);
        configureContainer?.Invoke(builder);
        try
        {
            await using IContainer container = builder.Build();
            try
            {
                await InitializeNodeAsync(container, config, cancellationToken);
                await ExercisePipelineAsync(container, chainSpec.Genesis!.Header, timestamp, config.GetConfig<IJsonRpcConfig>().Port, token, keys, cancellationToken);
            }
            finally
            {
                await StopNodeAsync(container, exitSource, warmMetrics);
            }
        }
        finally
        {
            exitSource.Exit(0);
        }
    }

    private static ChainSpec CreateChainSpec(ChainSpec source, UInt256 minGasPrice, PrivateKey[] keys)
    {
        // Preserve fork-dependent genesis fields loaded by the chain-spec loader, including Cancun-at-genesis chains.
        BlockHeader genesisHeader = source.Genesis!.Header.Clone();
        genesisHeader.StateRoot = Keccak.EmptyTreeHash;
        genesisHeader.TxRoot = Keccak.EmptyTreeHash;
        genesisHeader.ReceiptsRoot = Keccak.EmptyTreeHash;
        genesisHeader.Bloom = Bloom.Empty;
        genesisHeader.Difficulty = 0;
        genesisHeader.TotalDifficulty = 0;
        genesisHeader.GasLimit = 60_000_000;
        genesisHeader.GasUsed = 0;
        ChainSpec chainSpec = new()
        {
            Name = "Startup warmup",
            ChainId = source.ChainId,
            NetworkId = source.NetworkId,
            SealEngineType = source.SealEngineType,
            Parameters = source.Parameters,
            EngineChainSpecParametersProvider = source.EngineChainSpecParametersProvider,
            Genesis = new Block(genesisHeader, [], [], genesisHeader.WithdrawalsRoot is null ? null : []),
            TerminalTotalDifficulty = 0,
            Allocations = new Dictionary<Address, ChainSpecAllocation>
            {
                // Writes slots x and x + 0x80 for calldata x, reads x, then emits an empty LOG0.
                // Two writes per call take the storage trie past the parallel bulk-set threshold.
                [StorageContract] = new() { Code = [0x60, 1, 0x60, 0, 0x35, 0x55, 0x60, 1, 0x60, 0, 0x35, 0x60, 0x80, 0x01, 0x55, 0x60, 0, 0x35, 0x54, 0x50, 0x60, 0, 0x60, 0, 0xa0, 0] },
                [RevertContract] = new() { Code = [0x60, 0, 0x60, 0, 0xfd] },
                [Eip7002Constants.WithdrawalRequestPredeployAddress] = new() { Code = [0] },
                [Eip7251Constants.ConsolidationRequestPredeployAddress] = new() { Code = [0] },
                [Eip8282Constants.BuilderDepositRequestPredeployAddress] = new() { Code = [0] },
                [Eip8282Constants.BuilderExitRequestPredeployAddress] = new() { Code = [0] },
                [Eip4788Constants.BeaconRootsAddress] = new() { Code = [0] },
                [Eip2935Constants.BlockHashHistoryAddress] = new() { Code = Eip2935Constants.Code }
            }
        };
        if (UInt256.AddOverflow(minGasPrice, (UInt256)2_000_000_000, out UInt256 feeBudget)
            || UInt256.AddOverflow(feeBudget, genesisHeader.BaseFeePerGas, out feeBudget)
            || UInt256.MultiplyOverflow(feeBudget, (UInt256)60_000_000, out UInt256 balance)
            || UInt256.AddOverflow(balance, (UInt256)TransactionCount, out balance))
            throw new InvalidOperationException("Startup warmup cannot fund the configured minimum gas price.");
        for (int i = 0; i < SenderCount; i++) chainSpec.Allocations[keys[i].Address] = new() { Balance = balance };
        return chainSpec;
    }

    private static ConfigProvider CreateConfig(IConfigProvider liveConfig, bool flatState, string directory, bool authenticated, PrivateKey nodeKey)
    {
        IJsonRpcConfig liveRpcConfig = liveConfig.GetConfig<IJsonRpcConfig>();
        int port = GetFreeLoopbackPort();
        IMergeConfig mergeConfig = liveConfig.GetConfig<IMergeConfig>();
        ITxPoolConfig liveTxPoolConfig = liveConfig.GetConfig<ITxPoolConfig>();
        // Live values only for pipeline-shaping settings; ports, paths, and outward-facing services keep isolated defaults.
        return new ConfigProvider(
            liveConfig.GetConfig<IBlocksConfig>(),
            new TxPoolConfig
            {
                BlobsSupport = liveTxPoolConfig.BlobsSupport,
                PersistentBroadcastEnabled = liveTxPoolConfig.PersistentBroadcastEnabled,
                ProofsTranslationEnabled = liveTxPoolConfig.ProofsTranslationEnabled
            },
            liveConfig.GetConfig<IReceiptConfig>(),
            new JsonRpcConfig
            {
                Enabled = true,
                Port = port,
                RequestQueueLimit = liveRpcConfig.RequestQueueLimit,
                MaxConcurrentSharedRequests = liveRpcConfig.MaxConcurrentSharedRequests,
                JwtSecretFile = Path.Combine(directory, "jwt-secret"),
                UnsecureDevNoRpcAuthentication = !authenticated,
                // Modules that subscribe to block processing are created only when enabled.
                EnabledModules = [.. liveRpcConfig.EnabledModules.Union(liveRpcConfig.EngineEnabledModules).Append(ModuleType.Engine).Distinct()],
                PreloadRpcModules = false,
                CallsFilterFilePath = Path.Combine(directory, "jsonrpc.filter")
            },
            new InitConfig
            {
                BaseDbPath = directory,
                PipelineWarmupEnabled = false,
                DisableGcOnNewPayload = liveConfig.GetConfig<IInitConfig>().DisableGcOnNewPayload,
                AutoDump = DumpOptions.None,
                StaticNodesPath = Path.Combine(directory, "static-nodes.json"),
                TrustedNodesPath = Path.Combine(directory, "trusted-nodes.json")
            },
            new KeyStoreConfig { KeyStoreDirectory = Path.Combine(directory, "keystore"), TestNodeKey = nodeKey.ToString() },
            new NetworkConfig { LocalIp = "127.0.0.1", ExternalIp = "127.0.0.1", DiscoveryDns = null },
            new HealthChecksConfig { LowStorageSpaceShutdownThreshold = 0 },
            new DbConfig { SharedBlockCacheSize = 16 * 1024 * 1024, EnableMetricsUpdater = false },
            new FlatDbConfig
            {
                Enabled = flatState,
                Layout = liveConfig.GetConfig<IFlatDbConfig>().Layout,
                BlockCacheSizeBudget = 16 * 1024 * 1024,
                TrieCacheMemoryBudget = 16 * 1024 * 1024,
                PersistedSnapshotArenaPageCacheBytes = 16 * 1024 * 1024,
                ArenaFileSizeBytes = 16 * 1024 * 1024
            },
            new PruningConfig { CacheMb = 8, DirtyCacheMb = 4, DirtyNodeShardBit = 1 },
            new SyncConfig { FastSync = false, SnapSync = false },
            new MergeConfig
            {
                TerminalTotalDifficulty = "0",
                PrioritizeBlockLatency = mergeConfig.PrioritizeBlockLatency,
                CollectionsPerDecommit = mergeConfig.CollectionsPerDecommit,
                SweepMemory = mergeConfig.SweepMemory,
                CompactMemory = mergeConfig.CompactMemory,
                PostBlockGcDelayMs = mergeConfig.PostBlockGcDelayMs ?? (int)(mergeConfig.SecondsPerSlot * 1000 / 8)
            });
    }

    private static async Task InitializeNodeAsync(IContainer container, IConfigProvider config, CancellationToken cancellationToken)
    {
        // Reuse the live process's decoders without starting another peer network.
        await container.Resolve<EthereumStepsManager>().InitializeThrough(typeof(StartRpc), cancellationToken,
            typeof(InitTxTypesAndRlp), typeof(InitializeNetwork));
        container.Resolve<IMergeSyncController>().StopSyncing();
        container.Resolve<ISyncModeSelector>().Update();
        if (config.GetConfig<IInitConfig>().DisableGcOnNewPayload && config.GetConfig<IMergeConfig>().PrioritizeBlockLatency && !container.Resolve<IGCStrategy>().CanStartNoGCRegion())
            throw new InvalidOperationException("Startup warmup did not reach the production no-GC-region strategy.");
    }

    private static async Task ExercisePipelineAsync(IContainer container, BlockHeader genesisHeader, ulong timestamp, int port, string? token,
        PrivateKey[] keys, CancellationToken cancellationToken)
    {
        IMainProcessingContext main = container.Resolve<IMainProcessingContext>();
        EthereumJsonSerializer serializer = container.Resolve<EthereumJsonSerializer>();
        string address = $"http://127.0.0.1:{port}";
        using HttpClient client = new(new SocketsHttpHandler { UseProxy = false });
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // Consensus clients check the chain id on connect, which creates the Eth module and its block subscribers.
        await PostAsync(client, serializer, address, "eth_chainId", [], cancellationToken);
        // The pool validates against the head's fork, so move the head off the source genesis before submitting typed transactions.
        Block head = await BuildBlockAsync(container, genesisHeader, timestamp - 1, 0, cancellationToken);
        await SendPayloadAsync(client, serializer, address, head, container.Resolve<ISpecProvider>().GetSpec(head.Header), cancellationToken);
        SubmitTransactions(container, keys);
        Block block = await BuildBlockAsync(container, head.Header, timestamp, TransactionCount, cancellationToken);
        int processedTransactions = 0;
        int unexpectedReceipts = 0;
        main.TransactionProcessed += (_, args) =>
        {
            Interlocked.Increment(ref processedTransactions);
            if (args.TxReceipt.StatusCode != (args.Transaction.To == RevertContract ? 0 : 1))
                Interlocked.Increment(ref unexpectedReceipts);
        };
        await SendPayloadAsync(client, serializer, address, block, container.Resolve<ISpecProvider>().GetSpec(block.Header), cancellationToken);
        await main.BlockProcessingQueue.WaitUntilRemovedAsync(block.Hash!).AsTask().WaitAsync(cancellationToken);
        if (processedTransactions != block.Transactions.Length)
            throw new InvalidOperationException("Startup warmup did not execute every transaction through the payload pipeline.");
        if (unexpectedReceipts != 0)
            throw new InvalidOperationException("Startup warmup did not exercise the expected transfer, storage, and revert paths.");

        object call = new { from = keys[0].Address, to = StorageContract, gas = "0xf4240", input = new byte[32] };
        JsonElement callResult = await PostAsync(client, serializer, address, "eth_call", [call, "latest"], cancellationToken);
        if (callResult.GetString() != "0x")
            throw new InvalidOperationException("Startup warmup contract call returned unexpected data.");
    }

    private static async Task StopNodeAsync(IContainer container, ProcessExitSource exitSource, WarmMetrics warmMetrics)
    {
        exitSource.Exit(0);
        try
        {
            await container.Resolve<IServiceStopper>().StopAllServices();
        }
        finally
        {
            try
            {
                await container.Resolve<GCKeeper>().StopAsync();
            }
            finally
            {
                // Services have stopped queuing reports; storage must remain alive until their readers finish.
                await warmMetrics.DrainAsync();
            }
            warmMetrics.BestKnownNumber = container.Resolve<IBlockTree>().BestKnownNumber;
        }
    }

    /// <summary>Replaces a gauge value the warm node published with the live value, unless the live node has published since.</summary>
    /// <remarks>Retries while the live value moves, since the live processor publishes a height before its head reaches it.</remarks>
    internal static void ReplaceWarmValue(ref ulong gauge, ulong warmValue, Func<ulong> liveValue)
    {
        for (ulong expected = warmValue; ;)
        {
            ulong value = liveValue();
            if (Interlocked.CompareExchange(ref gauge, value, expected) != expected || liveValue() == value) return;
            expected = value;
        }
    }

    /// <summary>The height gauges the warm node published, and its outstanding processing reports.</summary>
    internal sealed class WarmMetrics
    {
        public int PendingReports;
        public ulong? PublishedHeight;
        public ulong? BestKnownNumber;

        /// <summary>Waits for queued reports before their storage dependencies can be disposed.</summary>
        public async Task DrainAsync()
        {
            while (Volatile.Read(ref PendingReports) > 0) await Task.Delay(1);
        }

        /// <summary>Waits for the warm node's queued reports, then gives its height gauges back to the live chain.</summary>
        public async Task RestoreLiveAsync(IBlockTree live)
        {
            await DrainAsync();
            if (PublishedHeight is { } height)
            {
                Func<ulong> liveHeight = () => live.Head?.Number ?? height;
                ReplaceWarmValue(ref BlockchainMetrics.Blocks, height, liveHeight);
                ReplaceWarmValue(ref BlockchainMetrics.BlockchainHeight, height, liveHeight);
            }
            if (BestKnownNumber is { } bestKnown)
                ReplaceWarmValue(ref BlockchainMetrics.BestKnownBlockNumber, bestKnown, () => live.BestKnownNumber);
        }
    }

    /// <summary>Production statistics that also track the reports they publish from the thread pool.</summary>
    internal sealed class WarmProcessingStats(IStateReader stateReader, ILogManager logManager, IBlocksConfig blocksConfig, WarmMetrics warmMetrics)
        : ProcessingStats(stateReader, logManager, blocksConfig), IProcessingStats
    {
        void IProcessingStats.UpdateStats(IReadOnlyList<Block> blocks, BlockHeader? baseBlock, long blockProcessingTimeInMicros)
        {
            // The base queues one report for each non-empty update.
            if (blocks.Count == 0) return;
            Interlocked.Increment(ref warmMetrics.PendingReports);
            try
            {
                UpdateStats(blocks, baseBlock, blockProcessingTimeInMicros);
            }
            catch
            {
                Interlocked.Decrement(ref warmMetrics.PendingReports);
                throw;
            }
        }

        protected override void GenerateReport(BlockData data)
        {
            try
            {
                base.GenerateReport(data);
            }
            finally
            {
                if (data.Block is { } block) warmMetrics.PublishedHeight = block.Number;
                Interlocked.Decrement(ref warmMetrics.PendingReports);
            }
        }
    }

    private static void SubmitTransactions(IContainer container, PrivateKey[] keys)
    {
        ISpecProvider specProvider = container.Resolve<ISpecProvider>();
        IReleaseSpec spec = container.Resolve<IChainHeadSpecProvider>().GetCurrentHeadSpec();
        IEthereumEcdsa ecdsa = container.Resolve<IEthereumEcdsa>();
        ITxPool txPool = container.Resolve<ITxPool>();
        PrivateKey authority = keys[SenderCount];
        BlockHeader head = container.Resolve<IBlockTree>().Head!.Header;
        UInt256 baseFee = spec.IsEip1559Enabled ? BaseFeeCalculator.Calculate(head, spec) : UInt256.Zero;
        UInt256 minimum = container.Resolve<IBlocksConfig>().MinGasPrice;
        UInt256 tip = minimum > 2_000_000_000 ? minimum : (UInt256)2_000_000_000;
        if (UInt256.AddOverflow(tip, baseFee, out UInt256 price))
            throw new InvalidOperationException("Startup warmup fee cap exceeds 256 bits.");
        for (int i = 0; i < TransactionCount; i++)
        {
            PrivateKey sender = keys[i % SenderCount];
            Transaction tx = CreateTransaction(i, sender, authority, specProvider.ChainId, spec, ecdsa, tip, price);
            AcceptTxResult accepted = txPool.SubmitTx(tx, TxHandlingOptions.PersistentBroadcast);
            if (!accepted) throw new InvalidOperationException($"Startup warmup transaction was not accepted: {accepted}");
        }
    }

    private static Transaction CreateTransaction(int i, PrivateKey sender, PrivateKey authority, ulong chainId, IReleaseSpec spec,
        IEthereumEcdsa ecdsa, UInt256 tip, UInt256 price)
    {
        TxType type = GetTransactionType(i, spec);
        byte[] slot = new byte[32];
        slot[^1] = (byte)i;
        Transaction tx = new()
        {
            Type = type,
            ChainId = chainId,
            Nonce = (ulong)(i / SenderCount),
            // Under Amsterdam pricing the authorization plus two fresh slots exceeds 250k gas.
            GasLimit = type == TxType.SetCode ? 1_000_000UL : 250_000UL,
            GasPrice = type is TxType.Legacy or TxType.AccessList ? price : tip,
            DecodedMaxFeePerGas = price,
            To = i switch { 0 => Address.Zero, 1 => RevertContract, 3 => authority.Address, _ => StorageContract },
            Value = 1,
            Data = slot,
            AccessList = type == TxType.Legacy ? null : new AccessList.Builder().AddAddress(StorageContract).AddStorage(new UInt256(slot, true)).Build(),
            AuthorizationList = type == TxType.SetCode ? [ecdsa.Sign(authority, chainId, StorageContract, 0)] : null,
            SenderAddress = sender.Address
        };
        ecdsa.Sign(sender, tx, spec.IsEip155Enabled);
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static TxType GetTransactionType(int index, IReleaseSpec spec) =>
        index == 2 && spec.IsEip7702Enabled ? TxType.SetCode
            : (index % 3) switch
            {
                1 when spec.IsEip2930Enabled => TxType.AccessList,
                2 when spec.IsEip1559Enabled => TxType.EIP1559,
                _ => TxType.Legacy
            };

    private static async Task<Block> BuildBlockAsync(IContainer container, BlockHeader parent, ulong timestamp, int transactionCount, CancellationToken cancellationToken)
    {
        IReleaseSpec spec = container.Resolve<ISpecProvider>().GetSpec(new ForkActivation(parent.Number + 1, timestamp));
        Withdrawal[]? withdrawals = null;
        if (spec.IsEip4895Enabled)
        {
            // Beacon-chain blocks carry 16 withdrawals.
            withdrawals = new Withdrawal[16];
            for (int i = 0; i < withdrawals.Length; i++)
                withdrawals[i] = new Withdrawal { Index = (ulong)i, ValidatorIndex = (ulong)i, Address = StorageContract, AmountInGwei = 1 };
        }

        PayloadAttributes attributes = new()
        {
            Timestamp = timestamp,
            PrevRandao = Keccak.Zero,
            SuggestedFeeRecipient = Address.Zero,
            Withdrawals = withdrawals,
            ParentBeaconBlockRoot = spec.IsEip4788Enabled ? Keccak.Zero : null,
            SlotNumber = spec.IsEip7843Enabled ? parent.Number + 1 : null
        };
        Block? block = await container.Resolve<IBlockProducer>().BuildBlock(parent, payloadAttributes: attributes, cancellationToken: cancellationToken);
        return block?.Transactions.Length == transactionCount ? block
            : throw new InvalidOperationException("Startup warmup block did not include every transaction.");
    }

    // Another process can take the port before the warm node binds it; the warmup then fails and startup continues.
    private static int GetFreeLoopbackPort()
    {
        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task SendPayloadAsync(HttpClient client, EthereumJsonSerializer serializer, string address, Block block, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        await SendNewPayloadAsync(client, serializer, address, block, spec, cancellationToken);
        await SendForkchoiceUpdatedAsync(client, serializer, address, block, spec, cancellationToken);
    }

    private static async Task SendNewPayloadAsync(HttpClient client, EthereumJsonSerializer serializer, string address, Block block, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        (string method, object[] parameters) = CreatePayloadRequest(block, spec);
        EnsureValid(await PostAsync(client, serializer, address, method, parameters, cancellationToken), method);
    }

    private static (string Method, object[] Parameters) CreatePayloadRequest(Block block, IReleaseSpec spec)
    {
        string method = spec.IsEip7805Enabled ? "engine_newPayloadV6"
            : spec.IsEip7928Enabled ? "engine_newPayloadV5"
            : spec.RequestsEnabled ? "engine_newPayloadV4"
            : spec.IsEip4844Enabled ? "engine_newPayloadV3"
            : spec.WithdrawalsEnabled ? "engine_newPayloadV2" : "engine_newPayloadV1";
        ExecutionPayload payload = spec.IsEip7928Enabled
            ? ExecutionPayloadV4.Create(block) : ExecutionPayloadV3.Create(block);
        object[] parameters = spec.IsEip7805Enabled
            ? [payload, Array.Empty<Hash256>(), Keccak.Zero, Array.Empty<byte[]>(), Array.Empty<byte[]>()]
            : spec.RequestsEnabled
            ? [payload, Array.Empty<Hash256>(), Keccak.Zero, Array.Empty<byte[]>()]
            : spec.IsEip4844Enabled ? [payload, Array.Empty<Hash256>(), Keccak.Zero] : [payload];
        return (method, parameters);
    }

    private static async Task SendForkchoiceUpdatedAsync(HttpClient client, EthereumJsonSerializer serializer, string address, Block block, IReleaseSpec spec, CancellationToken cancellationToken)
    {
        string forkchoiceUpdated = spec.IsEip7805Enabled ? "engine_forkchoiceUpdatedV5"
            : spec.IsEip7843Enabled ? "engine_forkchoiceUpdatedV4"
            : spec.IsEip4844Enabled ? "engine_forkchoiceUpdatedV3"
            : spec.WithdrawalsEnabled ? "engine_forkchoiceUpdatedV2" : "engine_forkchoiceUpdatedV1";
        object forkchoiceState = new { headBlockHash = block.Hash, safeBlockHash = block.Hash, finalizedBlockHash = block.Hash };
        EnsureValid((await PostAsync(client, serializer, address, forkchoiceUpdated, [forkchoiceState, null], cancellationToken)).GetProperty("payloadStatus"), forkchoiceUpdated);
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, EthereumJsonSerializer serializer, string address, string method, object?[] parameters,
        CancellationToken cancellationToken)
    {
        using StringContent content = new(serializer.Serialize(new { jsonrpc = "2.0", id = 1, method, @params = parameters }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await client.PostAsync(address, content, cancellationToken);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("result", out JsonElement result) ? result.Clone()
            : throw new InvalidOperationException($"Startup warmup {method} failed: {json}");
    }

    private static void EnsureValid(JsonElement payloadStatus, string method)
    {
        if (payloadStatus.GetProperty("status").GetString() != "VALID")
            throw new InvalidOperationException($"Startup warmup {method} returned {payloadStatus}");
    }
}
