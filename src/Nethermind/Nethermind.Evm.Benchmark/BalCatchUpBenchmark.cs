// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.Synchronization;
using MergeMetrics = Nethermind.Merge.Plugin.Metrics;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Catches up a segment of finalized Amsterdam blocks from the same pre-state either by applying each block's
/// EIP-7928 access list (<see cref="FinalizedBlockAccessListProcessor"/>) or by executing it, as a node that was
/// offline does with <c>Sync.ReconstructFinalizedStateFromBlockAccessLists</c> on or off.
/// </summary>
/// <remarks>
/// <para>Without environment variables the segment is synthetic and every database is in memory, so it runs anywhere.
/// Each block is produced once in <see cref="GlobalSetup"/> and its header takes the produced roots and access-list
/// hash, so the segment is a valid chain. <c>DevnetLike</c> approximates a glamsterdam-devnet-8 block (about 6 Mgas
/// over 66 transactions); <c>StorageHeavy</c> maximises written items per gas, the access list's worst case;
/// <c>ComputeHeavy</c> spends gas without touching state, its best case.</para>
/// <para>Environment variables, following <c>CodeLoadBenchmarks</c>:</para>
/// <list type="bullet">
///   <item><c>NETHERMIND_BALCATCHUP_DB</c>: keep state, blocks, receipts and access lists in RocksDB under this
///   directory. The synthetic segment is built in a fresh subdirectory; with a fixture it is the node's datadir
///   holding the pre-state, opened in place (pass a copy: headers, receipts and lists of the segment are added).</item>
///   <item><c>NETHERMIND_BALCATCHUP_FIXTURE</c>: a directory written by <c>scripts/bal-catchup-fixture.py</c>, whose
///   <c>segment.jsonl</c> holds real blocks with their access lists and receipts. The segment starts right above the
///   datadir's persisted state. Needs <c>NETHERMIND_BALCATCHUP_DB</c> and <c>NETHERMIND_BALCATCHUP_CHAINSPEC</c>.</item>
///   <item><c>NETHERMIND_BALCATCHUP_BLOCKS</c>: how many blocks to catch up (default 64, or the whole fixture).</item>
///   <item><c>NETHERMIND_BALCATCHUP_COLD=1</c>: reopen the databases before every iteration, so no in-process
///   cache is warm, as after a restart. On-disk only.</item>
///   <item><c>NETHERMIND_BALCATCHUP_DROP_CACHES=1</c>: on Linux, as root, also empty the page cache before every
///   iteration.</item>
/// </list>
/// <para>Both arms process the whole segment through <see cref="IBranchProcessor"/> with
/// <see cref="ProcessingOptions.StoreReceipts"/>; the off arm flips the runtime flag the policy reads, so execution
/// takes the regular validator path (parallel when enabled) with the list attached. One operation is the whole segment,
/// whose length setup prints; cleanup prints blocks/s, gas/s and splits each block into processing (execution, or the list's
/// application) and the state commit after it, where the state root's remaining hashing and the trie writes happen. On
/// disk it also times persisting the segment's state.
/// Flat-state trie warming runs with production settings.</para>
/// <para>Setup refuses to benchmark if the on arm falls back for any block or the off arm reconstructs any.</para>
/// </remarks>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class BalCatchUpBenchmark
{
    private const int SyntheticBlocks = 64;
    private const ulong BlockGasLimit = 100_000_000;
    private const int SlotsPerWrite = 10;
    private const int HashesPerCompute = 1_500;
    private const string FixtureShape = "Fixture";

    private static readonly string? DbRoot = Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_DB");
    private static readonly string? FixtureDir = Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_FIXTURE");
    private static readonly string? ChainSpecPath = Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_CHAINSPEC");
    private static readonly int? BlockLimit = int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_BLOCKS"), out int limit) ? limit : null;
    private static readonly bool Cold = Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_COLD") == "1";
    private static readonly bool DropCaches = OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_DROP_CACHES") == "1";

    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithWarmupCount(2)
                .WithIterationCount(10)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min, StatisticColumn.Median, StatisticColumn.P95);
        }
    }

    /// <summary>The shape of every block in the synthetic segment, or <c>Fixture</c> for real blocks.</summary>
    [ParamsSource(nameof(Shapes))]
    public string Shape { get; set; } = "DevnetLike";

    public static IEnumerable<string> Shapes => FixtureDir is null ? ["DevnetLike", "StorageHeavy", "ComputeHeavy"] : [FixtureShape];

    /// <summary>Whether finalized blocks are caught up from their access lists.</summary>
    [Params(true, false)]
    public bool BalCatchUp { get; set; }

    /// <summary>Whether state is held in the flat database, the client default, or the trie database. A fixture's
    /// datadir decides for itself and runs as flat.</summary>
    [ParamsSource(nameof(Backends))]
    public bool FlatDb { get; set; }

    public static IEnumerable<bool> Backends => FixtureDir is null ? [true, false] : [true];

    private static readonly Address WriterAddress = new("0x00000000000000000000000000000000000000b1");
    private static readonly Address HasherAddress = new("0x00000000000000000000000000000000000000b2");
    private static readonly byte[] StopCode = [0x00];

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;
    private readonly SyncConfig _syncConfig = new() { ReconstructFinalizedStateFromBlockAccessLists = true };
    private readonly FinalizedBeacon _beacon = new();
    private readonly BlockTiming _timing = new();
    private IContainer _container = null!;
    private IBranchProcessor _branch = null!;
    private BlockHeader _parent = null!;
    private Block[] _segment = [];
    private string? _dbPath;
    private ulong _nonce;
    private int _recipient;
    private long _segmentTicks;
    private long _iterations;

    private bool IsFixture => Shape == FixtureShape;

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (IsFixture && (DbRoot is null || ChainSpecPath is null))
            throw new InvalidOperationException("A fixture needs NETHERMIND_BALCATCHUP_DB (the datadir with the pre-state) and NETHERMIND_BALCATCHUP_CHAINSPEC.");
        if (Cold && DbRoot is null)
            throw new InvalidOperationException("NETHERMIND_BALCATCHUP_COLD needs NETHERMIND_BALCATCHUP_DB: in-memory databases do not survive reopening.");

        _dbPath = IsFixture ? DbRoot : DbRoot is null ? null : FreshDirectory(Path.Combine(DbRoot, $"{Shape}-{(FlatDb ? "flat" : "trie")}-{(BalCatchUp ? "on" : "off")}"));
        _syncConfig.ReconstructFinalizedStateFromBlockAccessLists = true;
        BuildContainer();

        if (IsFixture) LoadFixture();
        else BuildSyntheticSegment();

        _beacon.Finalized = _segment[^1].Hash;
        _syncConfig.ReconstructFinalizedStateFromBlockAccessLists = BalCatchUp;

        long balBefore = MergeMetrics.BalCatchUpBlocks;
        long fallbacksBefore = Fallbacks();
        CatchUp();
        long reconstructed = MergeMetrics.BalCatchUpBlocks - balBefore;
        long fallbacks = Fallbacks() - fallbacksBefore;
        if (reconstructed != (BalCatchUp ? _segment.Length : 0) || fallbacks != 0)
            throw new InvalidOperationException($"BalCatchUp={BalCatchUp}: {reconstructed} blocks reconstructed and {fallbacks} fell back, so the arm does not measure the path it names.");
        IterationCleanup();
        _timing.Reset();
        _segmentTicks = 0;
        _iterations = 0;

        ulong gas = _segment.Aggregate(0UL, static (sum, b) => sum + b.GasUsed);
        int txs = _segment.Sum(static b => b.Transactions.Length);
        int items = _segment.Sum(static b => b.BlockAccessList!.ItemCount);
        int n = _segment.Length;
        IFlatDbConfig flat = _container.Resolve<IFlatDbConfig>();
        Console.WriteLine($"// {Shape}: {n} blocks {_segment[0].Number}-{_segment[^1].Number}, {gas / (ulong)n:N0} gas/block, {txs / n} txs/block, {items / n} BAL items/block, " +
            $"{(_dbPath is null ? "in memory" : "on disk")}{(Cold ? ", cold" : "")}, trie warmers {flat.TrieWarmerWorkerCount}");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        if (Cold)
        {
            _container.Dispose();
            BuildContainer();
        }

        if (DropCaches) File.WriteAllText("/proc/sys/vm/drop_caches", "3");
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        foreach (Block block in _segment) block.DisposeAccountChanges();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_iterations > 0)
        {
            double blocks = _segment.Length * (double)_iterations;
            double gas = _segment.Aggregate(0UL, static (sum, b) => sum + b.GasUsed) * (double)_iterations;
            double totalMs = Stopwatch.GetElapsedTime(0, _segmentTicks).TotalMilliseconds;
            double processMs = _timing.ProcessMs;
            Console.WriteLine($"// {Shape} BalCatchUp={BalCatchUp} FlatDb={FlatDb}: {blocks / totalMs * 1000:N1} blocks/s, {gas / totalMs / 1000:N1} Mgas/s; " +
                $"per block {totalMs / blocks:N3} ms = processing {processMs / blocks:N3} ms + state commit {(totalMs - processMs) / blocks:N3} ms");
        }

        if (_dbPath is not null)
        {
            // Persisting moves the database past the pre-state, so it runs once, after the last iteration.
            long start = Stopwatch.GetTimestamp();
            _container.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);
            double persistMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"// {Shape} BalCatchUp={BalCatchUp} FlatDb={FlatDb}: persisting the segment's state took {persistMs:N0} ms, {persistMs / _segment.Length:N2} ms per block");
        }

        _container.Dispose();
        if (_dbPath is not null && !IsFixture) Directory.Delete(_dbPath, recursive: true);
    }

    [Benchmark]
    public Block[] CatchUp()
    {
        long start = Stopwatch.GetTimestamp();
        Block[] processed = _branch.Process(_parent, _segment, ProcessingOptions.StoreReceipts, NullBlockTracer.Instance);
        _segmentTicks += Stopwatch.GetTimestamp() - start;
        _iterations++;
        return processed;
    }

    private void BuildContainer()
    {
        ContainerBuilder builder = new();
        if (IsFixture)
        {
            ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(ChainSpecPath!);
            builder.AddModule(new TestNethermindModule(new ConfigProvider(new FlatDbConfig { Enabled = true }, new InitConfig { BaseDbPath = _dbPath! }),
                chainSpec, useTestSpecProvider: false));
        }
        else
        {
            builder.AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = FlatDb }, new InitConfig { BaseDbPath = _dbPath ?? "db" }))
                .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance));
        }

        builder
            .AddSingleton<ISyncConfig>(_syncConfig)
            .AddSingleton<IReceiptConfig>(new ReceiptConfig { StoreReceipts = true, DeferredPersistence = false })
            .AddSingleton<IBeaconSyncStrategy>(_beacon)
            .AddSingleton<FinalizedBlockAccessListPolicy>()
            .AddSingleton<IMainProcessingModule>(ctx => new FinalizedBlockAccessListModule(_syncConfig, ctx.Resolve<ISpecProvider>()))
            // Registered last, so it is the outermost decorator and times the whole block.
            .AddSingleton<IMainProcessingModule>(new TimingModule(_timing))
            // The test modules turn flat-state trie warming down to keep tests fast; a catch-up needs production settings.
            .Intercept<IFlatDbConfig>(static config =>
            {
                FlatDbConfig defaults = new();
                config.TrieWarmerWorkerCount = defaults.TrieWarmerWorkerCount;
                config.WarmReadConcurrency = defaults.WarmReadConcurrency;
            });

        if (_dbPath is not null)
        {
            // The test modules keep every database in memory; put them back on RocksDB under the datadir.
            builder
                .AddSingleton<IDbFactory, RocksDbFactory>()
                .AddColumnDatabase<FlatDbColumns>(DbNames.Flat, static settings => settings.PersistRepairMarkerUntilAcknowledged = true);
        }

        _container = builder.Build();
        _branch = _container.Resolve<MainProcessingContext>().BranchProcessor;
    }

    private void BuildSyntheticSegment()
    {
        MainProcessingContext main = _container.Resolve<MainProcessingContext>();
        IBlockTree tree = _container.Resolve<IBlockTree>();
        _parent = CreateGenesis(main.WorldState, tree);
        // On disk the pre-state has to be in the database, so a cold iteration that reopens it starts from there.
        if (_dbPath is not null) _container.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);
        _segment = ProduceSegment(tree, BlockLimit ?? SyntheticBlocks);

        IBlockAccessListStore lists = _container.Resolve<IBlockAccessListStore>();
        BlockAccessListRecoveryStep recovery = _container.Resolve<IEnumerable<IBlockPreprocessorStep>>().OfType<BlockAccessListRecoveryStep>().Single();
        foreach (Block block in _segment)
        {
            lists.Insert(block.Number, block.Hash!, block.EncodedBlockAccessList!);
            block.EncodedBlockAccessList = null;
            block.GeneratedBlockAccessList = null;
            recovery.RecoverDataForQueuedProcessing(block);
            if (block.BlockAccessList is null) throw new InvalidOperationException($"No access list attached to block {block.Number}.");
        }
    }

    /// <summary>Loads the fixture's blocks above the datadir's persisted state, stores their lists and receipts as
    /// forward sync does, and recovers senders and lists as queued processing does.</summary>
    private void LoadFixture()
    {
        IBlockTree tree = _container.Resolve<IBlockTree>();
        _parent = tree.Head?.Header ?? throw new InvalidOperationException($"{_dbPath} has no head block.");
        IBlockAccessListStore lists = _container.Resolve<IBlockAccessListStore>();
        IReceiptStorage receipts = _container.Resolve<IReceiptStorage>();
        ISpecProvider specProvider = _container.Resolve<ISpecProvider>();
        IBlockPreprocessorStep[] steps = [.. _container.Resolve<IEnumerable<IBlockPreprocessorStep>>()];

        List<Block> segment = [];
        BlockHeader parent = _parent;
        foreach (string line in File.ReadLines(Path.Combine(FixtureDir!, "segment.jsonl")))
        {
            FixtureBlock entry = JsonSerializer.Deserialize<FixtureBlock>(line)!;
            if (entry.number <= _parent.Number) continue;
            if (segment.Count == (BlockLimit ?? int.MaxValue)) break;

            Block block = Rlp.Decode<Block>(Bytes.FromHexString(entry.block)) ?? throw new InvalidDataException($"Fixture block {entry.number} does not decode.");
            block.Header.Hash = block.Header.CalculateHash();
            if (block.ParentHash != parent.Hash)
                throw new InvalidOperationException($"Fixture block {entry.number} does not extend {parent.ToString(BlockHeader.Format.Short)}: the fixture has to start right above the datadir's persisted state (block {_parent.Number}).");

            byte[] encodedList = Bytes.FromHexString(entry.bal);
            if (Keccak.Compute(encodedList) != block.Header.BlockAccessListHash)
                throw new InvalidOperationException($"Fixture block {entry.number}: the access list does not match the header.");
            TxReceipt[] blockReceipts = [.. entry.receipts.Select(r => Rlp.Decode<TxReceipt>(Bytes.FromHexString(r), RlpBehaviors.SkipTypedWrapping)!)];
            if (ReceiptsRootCalculator.Instance.GetReceiptsRoot(blockReceipts, specProvider.GetSpec(block.Header), block.ReceiptsRoot) != block.ReceiptsRoot)
                throw new InvalidOperationException($"Fixture block {entry.number}: the receipts do not match the header.");

            if (tree.FindHeader(block.Hash!, BlockTreeLookupOptions.None) is null) tree.Insert(block.Header);
            lists.Insert(block.Number, block.Hash!, encodedList);
            receipts.Insert(block, blockReceipts, ensureCanonical: false);
            foreach (IBlockPreprocessorStep step in steps) step.RecoverDataForQueuedProcessing(block);
            if (block.BlockAccessList is null) throw new InvalidOperationException($"No access list attached to block {block.Number}.");

            segment.Add(block);
            parent = block.Header;
        }

        if (segment.Count == 0) throw new InvalidOperationException($"The fixture has no blocks above the datadir's persisted state (block {_parent.Number}).");
        _segment = [.. segment];
    }

    private static string FreshDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        Directory.CreateDirectory(path);
        return path;
    }

    private static long Fallbacks()
    {
        long sum = 0;
        foreach (KeyValuePair<BalCatchUpFallbackReason, long> entry in MergeMetrics.BalCatchUpFallbacks) sum += entry.Value;
        return sum;
    }

    private BlockHeader CreateGenesis(IWorldState state, IBlockTree tree)
    {
        IReleaseSpec spec = Amsterdam.Instance;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(_senderKey.Address, 1_000_000_000.Ether);
            state.CreateAccount(WriterAddress, UInt256.Zero);
            state.InsertCode(WriterAddress, BuildWriterCode(), spec);
            state.CreateAccount(HasherAddress, UInt256.Zero);
            state.InsertCode(HasherAddress, BuildHasherCode(), spec);
            for (int i = 0; i < 4_096; i++) state.CreateAccount(Recipient(i), UInt256.One);
            foreach (Address predeploy in (Address[])[
                Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7251Constants.ConsolidationRequestPredeployAddress,
                Eip8282Constants.BuilderDepositRequestPredeployAddress, Eip8282Constants.BuilderExitRequestPredeployAddress])
            {
                state.CreateAccount(predeploy, UInt256.Zero);
                state.InsertCode(predeploy, StopCode, spec);
            }

            state.Commit(spec);
            state.RecalculateStateRoot();
            Block genesis = Build.A.Block.Genesis.WithStateRoot(state.StateRoot).WithGasLimit(BlockGasLimit).TestObject;
            state.CommitTree(0);
            tree.SuggestBlock(genesis, BlockTreeSuggestOptions.None);
            return genesis.Header;
        }
    }

    private Block[] ProduceSegment(IBlockTree tree, int count)
    {
        Block[] segment = new Block[count];
        BlockHeader parent = _parent;
        for (int i = 0; i < count; i++)
        {
            Block suggested = Build.A.Block.WithParent(parent).WithDifficulty(0).WithGasLimit(BlockGasLimit)
                .WithBaseFeePerGas(1.GWei).WithTimestamp(parent.Timestamp + 12).WithTransactions(BuildTransactions()).TestObject;
            suggested.Header.IsPostMerge = true;

            BlockReceiptsTracer tracer = new();
            Block produced = _branch.Process(parent, [suggested], ProcessingOptions.NoValidation, tracer)[0];
            foreach (TxReceipt receipt in tracer.TxReceipts)
            {
                if (receipt.StatusCode != StatusCode.Success)
                    throw new InvalidOperationException($"{Shape} transaction failed in block {produced.Number} ({receipt.Error}).");
            }

            BlockHeader header = produced.Header;
            header.Hash = header.CalculateHash();
            Block block = new(header, produced.Body) { EncodedBlockAccessList = produced.EncodedBlockAccessList };
            produced.DisposeAccountChanges();
            tree.Insert(header);
            _container.Resolve<IReceiptStorage>().Insert(block, tracer.TxReceipts.ToArray(), ensureCanonical: false);
            segment[i] = block;
            parent = header;
        }

        return segment;
    }

    private Transaction[] BuildTransactions() => Shape switch
    {
        "DevnetLike" => [.. Transfers(50), .. Calls(WriterAddress, 6), .. Calls(HasherAddress, 10)],
        "StorageHeavy" => [.. Calls(WriterAddress, 90)],
        "ComputeHeavy" => [.. Calls(HasherAddress, 200)],
        _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, null),
    };

    private IEnumerable<Transaction> Transfers(int count)
    {
        for (int i = 0; i < count; i++)
            yield return Sign(Build.A.Transaction.WithTo(Recipient(_recipient++ % 4_096)).WithValue(1.Wei).WithGasLimit(21_000));
    }

    private IEnumerable<Transaction> Calls(Address target, int count)
    {
        for (int i = 0; i < count; i++)
            yield return Sign(Build.A.Transaction.WithTo(target).WithGasLimit(target == WriterAddress ? 2_000_000UL : 150_000UL));
    }

    private Transaction Sign(TransactionBuilder<Transaction> builder) =>
        builder.WithType(TxType.EIP1559).WithNonce(_nonce++).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(_senderKey).TestObject;

    private static Address Recipient(int i) => new(Keccak.Compute(i.ToBigEndianByteArray()).Bytes[..20]);

    /// <summary>Writes <see cref="SlotsPerWrite"/> fresh slots after the counter in slot 0, then advances it.</summary>
    private static byte[] BuildWriterCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 1; i <= SlotsPerWrite; i++)
            code.PushData(1).PushData(0).Op(Instruction.SLOAD).PushData(i).Op(Instruction.ADD).Op(Instruction.SSTORE);
        return code.PushData(SlotsPerWrite).PushData(0).Op(Instruction.SLOAD).Op(Instruction.ADD).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done;
    }

    /// <summary>Hashes a memory word <see cref="HashesPerCompute"/> times without touching state.</summary>
    private static byte[] BuildHasherCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < HashesPerCompute; i++)
            code.PushData(32).PushData(0).Op(Instruction.KECCAK256).PushData(0).Op(Instruction.MSTORE);
        return code.Op(Instruction.STOP).Done;
    }

    // One line of segment.jsonl, as scripts/bal-catchup-fixture.py writes it.
    private sealed record FixtureBlock(ulong number, string block, string bal, string[] receipts);

    private sealed class BlockTiming
    {
        private long _processTicks;
        public double ProcessMs => Stopwatch.GetElapsedTime(0, _processTicks).TotalMilliseconds;
        public void Add(long ticks) => _processTicks += ticks;
        public void Reset() => _processTicks = 0;
    }

    private sealed class TimingModule(BlockTiming timing) : Autofac.Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder.AddDecorator<IBlockProcessor>((_, inner) => new TimingBlockProcessor(inner, timing));
    }

    /// <summary>Times each block's processing: execution, or the list's application with its state root.</summary>
    private sealed class TimingBlockProcessor(IBlockProcessor inner, BlockTiming timing) : IBlockProcessor
    {
        public event Action? TransactionsExecuted
        {
            add => inner.TransactionsExecuted += value;
            remove => inner.TransactionsExecuted -= value;
        }

        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block block, ProcessingOptions options, IBlockTracer blockTracer, IReleaseSpec spec, CancellationToken token = default)
        {
            long start = Stopwatch.GetTimestamp();
            (Block Block, TxReceipt[] Receipts) result = inner.ProcessOne(block, options, blockTracer, spec, token);
            timing.Add(Stopwatch.GetTimestamp() - start);
            return result;
        }
    }

    private sealed class FinalizedBeacon : IBeaconSyncStrategy
    {
        public Hash256? Finalized { get; set; }
        public bool ShouldBeInBeaconHeaders() => false;
        public bool ShouldBeInBeaconModeControl() => false;
        public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => true;
        public bool MergeTransitionFinished => true;
        public ulong? GetTargetBlockHeight() => null;
        public Hash256? GetFinalizedHash() => Finalized;
        public Hash256? GetHeadBlockHash() => Finalized;
    }
}
