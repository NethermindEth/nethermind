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

/// <summary>Compares finalized BAL catch-up with execution from the same pre-state.</summary>
/// <remarks>
/// Runs synthetic segments or exported fixtures through the production branch processor.
/// See scripts/bal-catchup-benchmark.md for inputs and measurement details.
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
    private const int WarmupCount = 2;
    // BenchmarkDotNet's jitting run and the job's warm-ups.
    private const int WarmupRuns = 1 + WarmupCount;

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
                .WithWarmupCount(WarmupCount)
                .WithIterationCount(FixtureDir is null ? 10 : 5)
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

    /// <summary>Whether state is held in the flat database, the client default, or the trie database. Fixtures need a
    /// flat datadir and always run as flat.</summary>
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
    private readonly List<SegmentBlock> _segmentData = [];
    private readonly List<(long Total, long Process)> _samples = [];
    private string? _pristinePath;
    private string? _workPath;
    private ulong _nonce;
    private int _recipient;

    private bool IsFixture => Shape == FixtureShape;
    private bool OnDisk => DbRoot is not null;
    private string CaseName => $"{Shape}-{(FlatDb ? "flat" : "trie")}";

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (IsFixture && (DbRoot is null || ChainSpecPath is null))
            throw new InvalidOperationException("A fixture needs NETHERMIND_BALCATCHUP_DB (the datadir with the pre-state) and NETHERMIND_BALCATCHUP_CHAINSPEC.");
        if (Cold && !OnDisk)
            throw new InvalidOperationException("NETHERMIND_BALCATCHUP_COLD needs NETHERMIND_BALCATCHUP_DB: in-memory databases do not survive reopening.");
        if (BlockLimit is <= 0)
            throw new InvalidOperationException("NETHERMIND_BALCATCHUP_BLOCKS must be positive.");
        if (IsFixture && !Directory.Exists(Path.Combine(DbRoot!, DbNames.Flat)))
            throw new InvalidOperationException($"{DbRoot} holds no flat state database; fixtures need a flat datadir.");

        if (!IsFixture) ValidateWarmLength(BlockLimit ?? SyntheticBlocks);

        try
        {
            SetUpSegment();
        }
        catch
        {
            CleanUpWorkingCopy();
            throw;
        }
    }

    private void SetUpSegment()
    {
        if (!IsFixture)
        {
            // Produced in memory, so the on-disk pre-state below holds nothing but genesis.
            BuildContainer(null);
            BuildSyntheticSegment();
            if (OnDisk)
            {
                _container.Dispose();
                _pristinePath = FreshDirectory(Path.Combine(DbRoot!, $"{CaseName}-pristine"));
                BuildContainer(_pristinePath);
                Hash256? genesis = CreateGenesis(_container.Resolve<MainProcessingContext>().WorldState, _container.Resolve<IBlockTree>()).Hash;
                if (genesis != _parent.Hash) throw new InvalidOperationException("The on-disk genesis differs from the one the segment was produced on.");
                _container.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);
                _container.Dispose();
            }
        }
        else
        {
            _pristinePath = DbRoot;
        }

        if (OnDisk) OpenWorkingCopy();
        if (IsFixture)
        {
            LoadFixture();
            ValidateWarmLength(_segmentData.Count);
        }
        InstallSegment();

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
        _samples.Clear();

        ulong gas = _segment.Aggregate(0UL, static (sum, b) => sum + b.GasUsed);
        int txs = _segment.Sum(static b => b.Transactions.Length);
        int items = _segment.Sum(static b => b.BlockAccessList!.ItemCount);
        int n = _segment.Length;
        IFlatDbConfig flat = _container.Resolve<IFlatDbConfig>();
        Console.WriteLine($"// {Shape}: {n} blocks {_segment[0].Number}-{_segment[^1].Number}, {gas / (ulong)n:N0} gas/block, {txs / n} txs/block, {items / n} BAL items/block, " +
            $"{(OnDisk ? "on disk" : "in memory")}{(Cold ? ", fresh process caches" : "")}{(DropCaches ? ", OS page cache dropped" : "")}, trie warmers {flat.TrieWarmerWorkerCount}");
    }

    private void ValidateWarmLength(int blocks)
    {
        // Warm flat runs must keep the replay parent above the finality-driven persistence boundary.
        ulong warmLimit = new FlatDbConfig().MinReorgDepth;
        if (FlatDb && !Cold && (ulong)blocks > warmLimit)
            throw new InvalidOperationException($"A warm run on flat state replays at most {warmLimit} blocks; set " +
                (OnDisk ? "NETHERMIND_BALCATCHUP_COLD=1 or " : "") + "NETHERMIND_BALCATCHUP_BLOCKS.");
    }

    [IterationSetup]
    public void IterationSetup()
    {
        if (Cold)
        {
            // Closing the databases persists what the last iteration left in memory, so every cold iteration starts
            // from a fresh copy of the pre-state instead.
            _container.Dispose();
            OpenWorkingCopy();
            InstallSegment();
        }

        if (DropCaches)
        {
            // Dirty pages survive a drop, so write them back first.
            using (Process sync = Process.Start("sync")) sync.WaitForExit();
            File.WriteAllText("/proc/sys/vm/drop_caches", "3");
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        foreach (Block block in _segment) block.DisposeAccountChanges();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        (long Total, long Process)[] samples = [.. _samples.Skip(_samples.Count > WarmupRuns ? WarmupRuns : 0).OrderBy(static s => s.Total)];
        if (samples.Length > 0)
        {
            (long total, long process) = samples[samples.Length / 2];
            double blocks = _segment.Length;
            double totalMs = Stopwatch.GetElapsedTime(0, total).TotalMilliseconds;
            double processMs = Stopwatch.GetElapsedTime(0, process).TotalMilliseconds;
            double gas = _segment.Aggregate(0UL, static (sum, b) => sum + b.GasUsed);
            Console.WriteLine($"// {Shape} BalCatchUp={BalCatchUp} FlatDb={FlatDb}: median of {samples.Length} runs {blocks / totalMs * 1000:N1} blocks/s, {gas / totalMs / 1000:N1} Mgas/s; " +
                $"per block {totalMs / blocks:N3} ms = processing {processMs / blocks:N3} ms + state commit {(totalMs - processMs) / blocks:N3} ms");
        }

        if (OnDisk)
        {
            // The working copy is discarded afterwards, so persisting it past the pre-state is harmless.
            long start = Stopwatch.GetTimestamp();
            _container.Resolve<IWorldStateManager>().FlushCache(CancellationToken.None);
            double persistMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"// {Shape} BalCatchUp={BalCatchUp} FlatDb={FlatDb}: persisting the segment's state took {persistMs:N0} ms, {persistMs / _segment.Length:N2} ms per block");
        }

        CleanUpWorkingCopy();
    }

    private void CleanUpWorkingCopy()
    {
        try
        {
            _container?.Dispose();
        }
        finally
        {
            if (_workPath is not null && Directory.Exists(_workPath)) Directory.Delete(_workPath, recursive: true);
            if (!IsFixture && _pristinePath is not null && Directory.Exists(_pristinePath)) Directory.Delete(_pristinePath, recursive: true);
        }
    }

    [Benchmark]
    public Block[] CatchUp()
    {
        long processBefore = _timing.ProcessTicks;
        long start = Stopwatch.GetTimestamp();
        Block[] processed = _branch.Process(_parent, _segment, ProcessingOptions.StoreReceipts, NullBlockTracer.Instance);
        _samples.Add((Stopwatch.GetTimestamp() - start, _timing.ProcessTicks - processBefore));
        return processed;
    }

    /// <summary>Copies the pristine pre-state to a working directory and opens it, so nothing the benchmark writes
    /// reaches the pre-state.</summary>
    private void OpenWorkingCopy()
    {
        _workPath = Path.Combine(WorkRoot, $"{CaseName}-{(BalCatchUp ? "on" : "off")}");
        if (Directory.Exists(_workPath)) Directory.Delete(_workPath, recursive: true);
        CopyDirectory(_pristinePath!, _workPath);
        BuildContainer(_workPath);
    }

    private static string WorkRoot => Environment.GetEnvironmentVariable("NETHERMIND_BALCATCHUP_WORKDIR")
        ?? (FixtureDir is null ? DbRoot! : Path.Combine(Path.GetTempPath(), "nethermind-balcatchup"));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)));
    }

    private void BuildContainer(string? dbPath)
    {
        ContainerBuilder builder = new();
        if (IsFixture)
        {
            ChainSpec chainSpec = new ChainSpecFileLoader(new EthereumJsonSerializer(), LimboLogs.Instance).LoadEmbeddedOrFromFile(ChainSpecPath!);
            builder.AddModule(new TestNethermindModule(new ConfigProvider(new FlatDbConfig { Enabled = true }, new InitConfig { BaseDbPath = dbPath! }),
                chainSpec, useTestSpecProvider: false));
        }
        else
        {
            builder.AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = FlatDb }, new InitConfig { BaseDbPath = dbPath ?? "db" }))
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

        if (dbPath is not null)
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
        _parent = CreateGenesis(main.WorldState, _container.Resolve<IBlockTree>());
        ProduceSegment(BlockLimit ?? SyntheticBlocks);
    }

    /// <summary>Stores the segment's headers, lists and receipts as forward sync does, and recovers senders and lists
    /// as queued processing does.</summary>
    private void InstallSegment()
    {
        IBlockTree tree = _container.Resolve<IBlockTree>();
        IBlockAccessListStore lists = _container.Resolve<IBlockAccessListStore>();
        IReceiptStorage receipts = _container.Resolve<IReceiptStorage>();
        IBlockPreprocessorStep[] steps = [.. _container.Resolve<IEnumerable<IBlockPreprocessorStep>>()];
        foreach ((Block block, byte[] encodedList, TxReceipt[] blockReceipts) in _segmentData)
        {
            if (tree.FindHeader(block.Hash!, BlockTreeLookupOptions.None) is null) tree.Insert(block.Header);
            lists.Insert(block.Number, block.Hash!, encodedList);
            receipts.Insert(block, blockReceipts, ensureCanonical: false);
            block.BlockAccessList = null;
            foreach (IBlockPreprocessorStep step in steps) step.RecoverDataForQueuedProcessing(block);
            if (block.BlockAccessList is null) throw new InvalidOperationException($"No access list attached to block {block.Number}.");
        }

        _segment = [.. _segmentData.Select(static s => s.Block)];
    }

    /// <summary>Loads the fixture's blocks above the working copy's persisted state.</summary>
    private void LoadFixture()
    {
        _parent = _container.Resolve<IBlockTree>().Head?.Header ?? throw new InvalidOperationException($"{DbRoot} has no head block.");
        ISpecProvider specProvider = _container.Resolve<ISpecProvider>();

        BlockHeader parent = _parent;
        foreach (string line in File.ReadLines(Path.Combine(FixtureDir!, "segment.jsonl")))
        {
            FixtureBlock entry = JsonSerializer.Deserialize<FixtureBlock>(line) ?? throw new InvalidDataException("Empty fixture line.");
            if (entry.number <= _parent.Number) continue;
            if (_segmentData.Count == (BlockLimit ?? int.MaxValue)) break;

            Block block = Rlp.Decode<Block>(Bytes.FromHexString(entry.block)) ?? throw new InvalidDataException($"Fixture block {entry.number} does not decode.");
            block.Header.Hash = block.Header.CalculateHash();
            if (block.ParentHash != parent.Hash)
                throw new InvalidOperationException($"Fixture block {entry.number} does not extend {parent.ToString(BlockHeader.Format.Short)}: the fixture has to start right above the datadir's persisted state (block {_parent.Number}).");

            byte[] encodedList = Bytes.FromHexString(entry.bal);
            if (Keccak.Compute(encodedList) != block.Header.BlockAccessListHash)
                throw new InvalidOperationException($"Fixture block {entry.number}: the access list does not match the header.");
            TxReceipt[] blockReceipts = [.. entry.receipts.Select(static r => Rlp.Decode<TxReceipt>(Bytes.FromHexString(r), RlpBehaviors.SkipTypedWrapping)!)];
            if (ReceiptsRootCalculator.Instance.GetReceiptsRoot(blockReceipts, specProvider.GetSpec(block.Header), block.ReceiptsRoot) != block.ReceiptsRoot)
                throw new InvalidOperationException($"Fixture block {entry.number}: the receipts do not match the header.");

            _segmentData.Add(new SegmentBlock(block, encodedList, blockReceipts));
            parent = block.Header;
        }

        if (_segmentData.Count == 0) throw new InvalidOperationException($"The fixture has no blocks above the datadir's persisted state (block {_parent.Number}).");
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

    private void ProduceSegment(int count)
    {
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
            // The next block opens its parent's state through the parent's header.
            _container.Resolve<IBlockTree>().Insert(header);
            _segmentData.Add(new SegmentBlock(new Block(header, produced.Body), produced.EncodedBlockAccessList!, [.. tracer.TxReceipts]));
            produced.DisposeAccountChanges();
            parent = header;
        }
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

    private sealed record SegmentBlock(Block Block, byte[] EncodedList, TxReceipt[] Receipts);

    // One line of segment.jsonl, as scripts/bal-catchup-fixture.py writes it.
    private sealed record FixtureBlock(ulong number, string block, string bal, string[] receipts);

    private sealed class BlockTiming
    {
        public long ProcessTicks { get; private set; }
        public void Add(long ticks) => ProcessTicks += ticks;
    }

    private sealed class TimingModule(BlockTiming timing) : Autofac.Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) =>
            builder.AddDecorator<IBlockProcessor>((_, inner) => new TimingBlockProcessor(inner, timing));
    }

    /// <summary>Times each block's processing: execution, or the list's application with its state root.</summary>
    private sealed class TimingBlockProcessor(IBlockProcessor inner, BlockTiming timing) : IBlockProcessor
    {
        public event Action<Block>? TransactionsExecuted
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
