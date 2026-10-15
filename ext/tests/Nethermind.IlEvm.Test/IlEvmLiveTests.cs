// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// Live profiling: which source's frames are counted under each Prefer, and only in the branches the scheduler marks; the
/// sampling estimator under threads; the lazy decay; the scorer's promotions, its noise, its demotions and cooldowns; the
/// live budget, its density eviction and the profile table's caps; and, end to end, a contract hot in calls promoted in a
/// window and run compiled with Prefer=Rpc, never with Prefer=Blocks, and one hot in blocks promoted with Prefer=Blocks.
/// </summary>
[NonParallelizable]
public class IlEvmLiveTests : IlEvmAdaptiveFixture
{
    /// <summary>Old blocks' timestamps are off the tip; these are at it: the hand-moved wall clock's second.</summary>
    private ulong TipTimestamp => (ulong)(_time.UnixMs / 1000);

    /// <summary>A clock tests move by hand, in milliseconds; the wall clock moves with it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        public long Ms = 1_000_000;

        public long UnixMs => 1_800_000_000_000 - 1_000_000 + Ms;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Ms;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(UnixMs);

        public void Advance(long ms) => Ms += ms;
    }

    private ManualTime _time = null!;
    private IBranchProcessor _branch = null!;
    private IChainHeadInfoProvider _head = null!;
    private IlScheduler _scheduler = null!;
    private IDb _codeDb = null!;
    private ulong _number = 100;

    [SetUp]
    public void LiveProfiling()
    {
        IlEvmSettings.EagerChunks = false;
        IlEvmSettings.CompileSynchronously = false;
        IlEvmSettings.Gate = true;
        IlCheckClock.ManualTicks = true;
        IlGate.Chaos = false;
        IlGate.Block = IlGate.BlockPrior;
        IlGate.Query = IlGate.QueryPrior;
        // The priors, whatever the environment overrides, and a tick that takes whatever it needs.
        IlLive.Rpc = IlLiveParams.RpcPrior;
        IlLive.Blocks = IlLiveParams.BlocksPrior;
        IlLive.BudgetBytes = 128L << 20;
        IlLive.TickBudgetMs = 60_000;
        IlRuntime.ManualRpcEpoch = 100;
        // The production rates: one transaction in 8 sampled on the queries' runner, one in 16 on block processing's.
        IlGate.QuerySampleEvery = 8;
        IlGate.BlockSampleEvery = 16;
        IlThread.Current.ResetForTests();
        IlThread.Current.Reseed(4242);
    }

    /// <summary>Counts calldata's first word down to zero: 10 opcodes a turn and 10 around them, in one part.</summary>
    private static byte[] CountLoopCode() => Nethermind.Core.Extensions.Bytes.FromHexString("6000355b8015601057600190036003565b5000");

    /// <summary>A calldata word of <paramref name="value"/>.</summary>
    private static byte[] Word(int value)
    {
        byte[] word = new byte[32];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(word.AsSpan(28), value);
        return word;
    }

    /// <summary><paramref name="code"/> made unique by <paramref name="tag"/> and padded with bytes no frame reaches to <paramref name="size"/>.</summary>
    private static byte[] Padded(byte[] code, byte tag, int size)
    {
        byte[] unique = Unique(code, tag);
        return [.. unique, .. new byte[Math.Max(0, size - unique.Length)]];
    }

    /// <summary>The source switch as the module sets it: Prefer, and the queries' counting with Prefer=Rpc.</summary>
    private static void Prefer(IlEvmPrefer prefer)
    {
        IlEvmSettings.Prefer = prefer;
        IlRuntime.QueryCounting = prefer == IlEvmPrefer.Rpc;
        IlRuntime.BlocksCounting = false;
    }

    /// <summary>A started scheduler on a hand-moved clock, attached to substitutes of block processing and the chain head, with a code database; with <paramref name="scorer"/>, live profiling ticks at its maintenance.</summary>
    private void Attach(bool scorer)
    {
        _time = new ManualTime();
        _branch = Substitute.For<IBranchProcessor>();
        _head = Substitute.For<IChainHeadInfoProvider>();
        _head.IsSyncing.Returns(false);
        _codeDb = TestMemDbProvider.Init().CodeDb;
        _scheduler = IlScheduler.Attach(new IlSchedulerOptions(250, 300, 1, 1, 12), _branch, Substitute.For<IBlockTree>(), _head, null, _codeDb, time: _time, threads: false);
        _scheduler.Start();
        if (scorer)
            IlScheduler.ScorerTick = IlLive.Tick;
    }

    /// <summary>Forgets every profile and decision, and attaches a new scheduler.</summary>
    private void Fresh(bool scorer = false)
    {
        IlEvm.ClearPrecompiled();
        IlThread.Current.ResetForTests();
        IlThread.Current.Reseed(4242);
        Attach(scorer);
    }

    private Block[] Branch(ulong timestamp, int count = 1) =>
        [.. Enumerable.Range(0, count).Select(i => Build.A.Block.WithNumber(_number++).WithTimestamp(timestamp - (ulong)(count - 1 - i)).TestObject)];

    /// <summary>Block processing starts <paramref name="blocks"/>, and each of them.</summary>
    private void Begin(Block[] blocks)
    {
        _branch.BlocksProcessing += Raise.EventWith(_branch, new BlocksProcessingEventArgs(blocks));
        foreach (Block block in blocks)
            _branch.BlockProcessing += Raise.EventWith(_branch, new BlockEventArgs(block));
    }

    private void Complete(Block[] blocks) =>
        _branch.BranchProcessingCompleted += Raise.EventWith(_branch, new BranchProcessingCompletedEventArgs(blocks, blocks.Length));

    /// <summary>Moves the clock to <paramref name="until"/>, pumping the window worker as its thread would.</summary>
    private void PumpUntil(long until)
    {
        while (_time.Ms < until)
        {
            if (!_scheduler.Pump(0))
                _time.Advance(Math.Min(10, until - _time.Ms));
        }

        _scheduler.Pump(0);
    }

    /// <summary>Pumps the window worker until it starts no step.</summary>
    private void Drain()
    {
        while (_scheduler.Pump(0))
        {
        }
    }

    /// <summary>A profile for <paramref name="code"/>, stamped and in the code database, last met on Osaka's body set: what a counting runner makes.</summary>
    private IlCodeProfile ProfileOf(byte[] code)
    {
        ValueHash256 hash = ValueKeccak.Compute(code);
        _codeDb[hash.Bytes] = code;
        IlCodeProfile profile = IlRuntime.Profiles.GetOrAdd(IlProfileKey.ByHash(hash, code.Length), static (key, length) => new IlCodeProfile(key, length), code.Length);
        profile.LastBodies = Bodies(Spec);
        return profile;
    }

    /// <summary>Counts <paramref name="count"/> folded visits of <paramref name="ops"/> opcodes each, standing for <paramref name="weight"/> visits, in the queries' epoch now.</summary>
    private static void Visits(IlCodeProfile profile, int count, int ops, int weight = 1, bool block = false, bool main = true)
    {
        for (int i = 0; i < count; i++)
            IlLive.Count(profile, ops, parts: 1, main, weight, block);
    }

    /// <summary>The weighted visits counted on <paramref name="profile"/> of one kind, block processing's own thread's or its others', by bucket summed; 0 for none.</summary>
    private static long Counted(IlCodeProfile profile, int kind = -1)
    {
        if (profile.Counters is not { } counters)
            return 0;
        long visits = 0;
        for (int k = 0; k < 2; k++)
        {
            for (int bucket = 0; bucket < IlLive.Buckets; bucket++)
            {
                if (kind < 0 || kind == k)
                    visits += counters[(k * IlLive.Buckets + bucket) * 2];
            }
        }

        return visits;
    }

    private static IlCodeProfile? Profiled(byte[] code) =>
        IlRuntime.Profiles.TryGetValue(IlProfileKey.ByHash(ValueKeccak.Compute(code), code.Length), out IlCodeProfile? profile) ? profile : null;

    [Test]
    public void Prefer_routing()
    {
        // The gate off, so every visit runs compiled and counts once, and every transaction sampled: the counts are exact.
        IlEvmSettings.Gate = false;
        IlGate.QuerySampleEvery = IlGate.BlockSampleEvery = 1;
        byte[] input = Word(40);
        byte tag = 0x01;
        foreach (IlEvmPrefer prefer in new[] { IlEvmPrefer.Rpc, IlEvmPrefer.Blocks, IlEvmPrefer.None })
        {
            Fresh();
            Prefer(prefer);
            IlEvmSettings.Gate = false;
            byte[] blockCode = Unique(CountLoopCode(), tag++), queryCode = Unique(CountLoopCode(), tag++), shared = Unique(CountLoopCode(), tag++);
            byte[] unshipped = Unique(CountLoopCode(), tag++);
            Ship(blockCode, queryCode, shared);
            CodeInfo blockInfo = Stamped(blockCode), queryInfo = Stamped(queryCode), sharedInfo = Stamped(shared), unshippedBlock = Stamped(unshipped);

            // A single block at the tip, not syncing: the branch Prefer=Blocks counts. Block frames on its own thread
            // and on another, queries beside them.
            Block[] blocks = Branch(TipTimestamp);
            Begin(blocks);
            for (int i = 0; i < 6; i++)
            {
                OnBlockThread(() => Run(BlockVm, blockInfo, input));
                Run(BlockVm, blockInfo, input);
                OnBlockThread(() => Run(BlockVm, sharedInfo, input));
                Run(BlockVm, sharedInfo, input);
                OnBlockThread(() => Run(BlockVm, unshippedBlock, input));
                Run(QueryVm, queryInfo, input);
                Run(QueryVm, sharedInfo, input);
            }

            Complete(blocks);
            IlThread.Current.FoldAll();
            IlCodeProfile blockProfile = Profiled(blockCode)!, queryProfile = Profiled(queryCode)!, sharedProfile = Profiled(shared)!;
            switch (prefer)
            {
                case IlEvmPrefer.Rpc:
                    Assert.That((Counted(blockProfile), Counted(queryProfile), Counted(sharedProfile)), Is.EqualTo((0L, 6L, 6L)), "only the queries count");
                    Assert.That(Counted(sharedProfile, kind: 1), Is.Zero, "the queries count one kind");
                    Assert.That((queryProfile.LastEpoch, queryProfile.Epochs), Is.EqualTo((100, 1)), "in the queries' epochs, seconds");
                    // The hook diet: code only block frames met is declined from its mark, with no profile.
                    Assert.That(IlEvmFrameRunner.CodeSlot(unshippedBlock), Is.InstanceOf<IlQuiet>());
                    Assert.That(Profiled(unshipped), Is.Null);
                    break;
                case IlEvmPrefer.Blocks:
                    Assert.That((Counted(blockProfile), Counted(queryProfile), Counted(sharedProfile)), Is.EqualTo((12L, 0L, 12L)), "only block processing counts");
                    Assert.That((Counted(blockProfile, kind: 0), Counted(blockProfile, kind: 1)), Is.EqualTo((6L, 6L)), "its own thread's, and its others'");
                    Assert.That((blockProfile.LastEpoch, IlRuntime.BlockEpoch), Is.EqualTo((1, 1)), "in counted blocks");
                    // Code block frames met while counting has a profile, each visit of a sampled transaction counted;
                    // between branches the block runner counts nothing, and the queries' runner never does: its code is
                    // declined from its mark.
                    Assert.That(Profiled(unshipped), Is.Not.Null);
                    Assert.That(Counted(Profiled(unshipped)!), Is.EqualTo(6));
                    byte[] betweenCode = Unique(CountLoopCode(), tag++), queryOnly = Unique(CountLoopCode(), tag++);
                    CodeInfo betweenInfo = Stamped(betweenCode), queryOnlyInfo = Stamped(queryOnly);
                    OnBlockThread(() => Run(BlockVm, betweenInfo, input));
                    Run(QueryVm, queryOnlyInfo, input);
                    Assert.That((IlEvmFrameRunner.CodeSlot(betweenInfo), IlEvmFrameRunner.CodeSlot(queryOnlyInfo)), Is.EqualTo((IlRuntime.QuietAll, IlRuntime.QuietAll)));
                    break;
                default:
                    Assert.That((Counted(blockProfile), Counted(queryProfile), Counted(sharedProfile)), Is.EqualTo((0L, 0L, 0L)), "nothing counts");
                    Assert.That(IlEvmFrameRunner.CodeSlot(unshippedBlock), Is.SameAs(IlRuntime.QuietAll));
                    break;
            }

            // The scorer serves the source Prefer picks, and none with Prefer=None.
            Assert.That(IlLive.Active, Is.SameAs(prefer switch { IlEvmPrefer.Rpc => IlLive.Rpc, IlEvmPrefer.Blocks => IlLive.Blocks, _ => null }));
        }
    }

    [Test]
    public void Blocks_counting_scope()
    {
        Fresh();
        Prefer(IlEvmPrefer.Blocks);
        IlEvmSettings.Gate = false;
        IlGate.BlockSampleEvery = 1;
        byte[] code = Unique(CountLoopCode(), 0x10);
        Ship(code);
        CodeInfo codeInfo = Stamped(code);
        IlCodeProfile profile = Profiled(code)!;
        byte[] input = Word(40);

        long CountedNow()
        {
            IlThread.Current.FoldAll();
            return Counted(profile);
        }

        void Frames()
        {
            OnBlockThread(() => Run(BlockVm, codeInfo, input));
            Run(BlockVm, codeInfo, input);
        }

        // Outside a branch, as the mempool's session or block production run: nothing counts.
        Frames();
        Assert.That(CountedNow(), Is.Zero);

        // A branch of three blocks catches up: nothing counts, nor are its blocks epochs.
        Block[] blocks = Branch(TipTimestamp, count: 3);
        Begin(blocks);
        Frames();
        Complete(blocks);
        Assert.That((CountedNow(), IlRuntime.BlockEpoch), Is.EqualTo((0L, 0)));

        // Nor while syncing.
        _head.IsSyncing.Returns(true);
        Begin(blocks = Branch(TipTimestamp));
        Frames();
        Complete(blocks);
        Assert.That((CountedNow(), IlRuntime.BlockEpoch), Is.EqualTo((0L, 0)));

        // Synced, a branch of two: counted, each block an epoch, and only between the branch's start and its completion.
        _head.IsSyncing.Returns(false);
        Begin(blocks = Branch(TipTimestamp, count: 2));
        Frames();
        Assert.That(IlRuntime.BlocksCounting, Is.True);
        Complete(blocks);
        Assert.That(IlRuntime.BlocksCounting, Is.False);
        Frames();
        Assert.That((CountedNow(), IlRuntime.BlockEpoch, profile.LastEpoch, profile.Epochs), Is.EqualTo((2L, 2, 2, 1)));
        Assert.That(IlLive.Touched.ToArray(), Is.EqualTo(new[] { profile }), "on the touched queue once");
    }

    [Test]
    public void Sampling_estimator()
    {
        // Sixteen threads, a million transactions each with a visit of two parts and 1,000 opcodes on one code: the queries'
        // runner samples one transaction in 8, block processing's one in 16, each visit of a sampled one standing for the
        // rest, and the threads add racily.
        const int Threads = 16, VisitsPerThread = 62_500, Ops = 1_000, Total = Threads * VisitsPerThread;
        EthereumVirtualMachine[] machines = [.. Enumerable.Range(0, Threads).Select(_ => NewQueryMachine())];
        OpcodeBodySet bodies = Bodies(Spec);
        IlGate.QuerySampleEvery = 8;
        IlGate.BlockSampleEvery = 16;
        foreach (bool block in new[] { false, true })
        {
            IlEvm.ClearPrecompiled();
            IlRuntime.BlockEpoch = 5;
            IlRuntime.QueryCounting = !block;
            IlRuntime.BlocksCounting = block;
            IlCodeProfile profile = new(IlProfileKey.ByContent(block ? 2 : 1, 100), 100);
            CodeInfo code = new(Unique(CountLoopCode(), block ? (byte)0x12 : (byte)0x11));
            IlThread[] gates = new IlThread[Threads];
            long[] sampledTransactions = new long[Threads];
            Thread[] threads = [.. Enumerable.Range(0, Threads).Select(t => new Thread(() =>
            {
                IlThread gate = IlThread.Current;
                gate.ResetForTests();
                gate.Reseed((uint)t + 1);
                gates[t] = gate;
                // Half the block threads are block processing's own, half its others.
                ProcessingThread.IsBlockProcessingThread = block && t % 2 == 0;
                IlEvm.IlCodeState state = new() { Profile = profile };
                EthereumVirtualMachine vm = machines[t];
                for (int i = 0; i < VisitsPerThread; i++)
                {
                    // Each visit in a transaction of its own, whose top-level entry draws; a sampled one's two parts of 500
                    // opcodes each are counted, the others' never reach the gate.
                    vm.OpCodeCount = 0;
                    IlThread.TopLevelEntry(block);
                    if (IlThread.SamplingTransaction)
                    {
                        sampledTransactions[t]++;
                        if (gate.Sample(vm, state, code, bodies, pc: 1, block))
                            gate.FinishReport(code, Ops / 2);
                        vm.OpCodeCount = Ops / 2;
                        if (gate.Sample(vm, state, code, bodies, pc: 2, block))
                            gate.FinishReport(code, Ops / 2);
                    }

                    vm.OpCodeCount = Ops;
                    // The rest of the transaction's work.
                    Thread.SpinWait(20);
                }

                gate.FoldAll();
            })
            { IsBackground = true })];
            foreach (Thread thread in threads)
                thread.Start();
            foreach (Thread thread in threads)
                thread.Join();

            double rate = block ? 16.0 : 8.0;
            long[] counters = profile.Counters!;
            int at = IlLive.BucketOf(Ops) * 2;
            int other = (IlLive.Buckets + IlLive.BucketOf(Ops)) * 2;
            long visits = counters[at] + counters[other], ops = counters[at + 1] + counters[other + 1];
            Assert.That((double)sampledTransactions.Sum(), Is.EqualTo(Total / rate).Within(5).Percent, $"block {block}: transactions sampled");
            Assert.That((double)visits, Is.EqualTo(Total).Within(5).Percent, $"block {block}: weighted visits");
            Assert.That((double)ops, Is.EqualTo((double)Total * Ops).Within(5).Percent, "weighted opcodes");
            Assert.That((double)counters[IlLive.PartsIndex], Is.EqualTo(2.0 * Total).Within(5).Percent, "weighted parts");
            Assert.That((double)counters[IlLive.MeasuredIndex], Is.EqualTo(Total / rate).Within(5).Percent, "measured visits: every visit of a sampled transaction");
            if (block)
            {
                Assert.That((double)counters[at], Is.EqualTo(Total / 2.0).Within(5).Percent, "block processing's own thread");
                Assert.That((double)counters[other], Is.EqualTo(Total / 2.0).Within(5).Percent, "its others");
            }
            else
            {
                Assert.That(counters[other], Is.Zero, "the queries count one kind");
            }

            Assert.That(IlLive.Touched.Count(p => ReferenceEquals(p, profile)), Is.EqualTo(1), "touched once in the epoch, however many visits");
            Assert.That(gates.Sum(static g => g.Counters.VisitsSampled), Is.EqualTo(sampledTransactions.Sum()), "a visit sampled for each sampled transaction");
        }
    }

    [Test]
    public void Counts_by_bucket_kind_and_weight()
    {
        // Under 64 opcodes the first bucket, then each power of two, from 65,536 the last.
        Assert.That(new long[] { 0, 1, 63, 64, 127, 128, 1_000, 65_535, 65_536, 1L << 40 }.Select(IlLive.BucketOf),
            Is.EqualTo(new[] { 0, 0, 0, 1, 1, 2, 4, 10, 11, 11 }));
        IlRuntime.BlockEpoch = 7;
        IlCodeProfile profile = new(IlProfileKey.ByContent(9, 10), 10);
        IlLive.Count(profile, ops: 100, parts: 3, main: true, weight: 16, block: true);
        IlLive.Count(profile, ops: 70_000, parts: 1, main: false, weight: 1, block: true);
        long[] counters = profile.Counters!;
        Assert.That((counters[1 * 2], counters[1 * 2 + 1]), Is.EqualTo((16L, 1_600L)), "block processing's own thread, bucket 1, weighted");
        Assert.That((counters[(IlLive.Buckets + 11) * 2], counters[(IlLive.Buckets + 11) * 2 + 1]), Is.EqualTo((1L, 70_000L)), "its others, the last bucket");
        Assert.That((counters[IlLive.PartsIndex], counters[IlLive.MeasuredIndex]), Is.EqualTo((49L, 2L)));
        Assert.That(counters.Sum(), Is.EqualTo(16 + 1_600 + 1 + 70_000 + 49 + 2));
        Assert.That((profile.FirstEpoch, profile.LastEpoch, profile.Epochs), Is.EqualTo((7, 7, 1)));
    }

    [Test]
    public void Decay()
    {
        foreach (IlLiveParams source in new[] { IlLive.Rpc, IlLive.Blocks })
        {
            IlEvm.ClearPrecompiled();
            void At(int epoch)
            {
                if (source.Block)
                    IlRuntime.BlockEpoch = epoch;
                else
                    IlRuntime.ManualRpcEpoch = epoch;
            }

            // The same counts on two profiles: one folded at every epoch, the other only in the epochs it is counted in,
            // decaying across the rest at once; and the sum by hand.
            IlCodeProfile stepped = new(IlProfileKey.ByContent(1, 10), 10), lazy = new(IlProfileKey.ByContent(2, 10), 10);
            Dictionary<int, int> counts = new() { [3] = 5, [4] = 2, [9] = 7, [17] = 1, [200] = 30, [513] = 4, [700] = 9 };
            int[] lazyFolds = [.. counts.Keys, 1_000];
            double byHand = 0;
            for (int epoch = 3; epoch <= 1_000; epoch++)
            {
                At(epoch);
                byHand *= Math.Pow(2, -1 / source.HalfLife);
                if (counts.TryGetValue(epoch, out int visits))
                {
                    Visits(stepped, visits, ops: 100, block: source.Block);
                    Visits(lazy, visits, ops: 100, block: source.Block);
                    byHand += visits;
                }

                IlLive.Fold(stepped, epoch, source);
                if (lazyFolds.Contains(epoch))
                    IlLive.Fold(lazy, epoch, source);
            }

            int index = IlLive.BucketOf(100) * 2;
            Assert.That(IlLive.Decayed(lazy, index, 1_000, source), Is.EqualTo(byHand).Within(1e-9).Percent, $"{source.Name}: lazy decay");
            Assert.That(IlLive.Decayed(stepped, index, 1_000, source), Is.EqualTo(byHand).Within(1e-9).Percent, "step by step");
            Assert.That(IlLive.Decayed(lazy, index + 1, 1_000, source), Is.EqualTo(100 * byHand).Within(1e-9).Percent, "the opcodes decay alike");

            // A half-life halves exactly; decayed lazily to any later epoch.
            IlCodeProfile once = new(IlProfileKey.ByContent(3, 10), 10);
            At(2_000);
            Visits(once, 1_024, ops: 100, block: source.Block);
            IlLive.Fold(once, 2_000, source);
            Assert.That(IlLive.Decayed(once, index, 2_000 + (int)source.HalfLife, source), Is.EqualTo(512).Within(1e-9).Percent, "a half-life");
            Assert.That(IlLive.Decayed(once, index, 2_000 + 3 * (int)source.HalfLife, source), Is.EqualTo(128).Within(1e-9).Percent, "three");

            // A history's epochs, one count each, add up to the decayed sum by hand.
            foreach (int epochs in new[] { 1, 2, 10, 600, 5_000 })
            {
                double sum = Enumerable.Range(0, epochs).Sum(i => Math.Pow(2, -i / source.HalfLife));
                Assert.That(IlLive.Window(epochs, source), Is.EqualTo(sum).Within(1e-9).Percent, $"window of {epochs}");
            }
        }
    }

    [Test]
    public void Reuse_distance_decays_with_the_visits()
    {
        // The visits over every profile, per kind, decay as each profile's do: they are the other visits between two of
        // one code's.
        IlLiveParams source = IlLive.Rpc;
        IlCodeProfile busy = new(IlProfileKey.ByContent(11, 22), 22), rare = new(IlProfileKey.ByContent(12, 22), 22);
        IlRuntime.ManualRpcEpoch = 100;
        Visits(busy, 1_000, ops: 20_000);
        IlLive.Fold(busy, 100, source);
        Assert.That(IlLive.AllVisits(0, 100, source), Is.EqualTo(1_000));
        IlRuntime.ManualRpcEpoch = 700;
        Visits(rare, 10, ops: 20_000);
        IlLive.Fold(rare, 700, source);
        Assert.That(IlLive.AllVisits(0, 700, source), Is.EqualTo(510).Within(1e-9).Percent, "a half-life on, the busy code's half and the rare one's");
        Assert.That(IlLive.AllVisits(0, 1_300, source), Is.EqualTo(255).Within(1e-9).Percent);
        Assert.That(IlLive.AllVisits(1, 1_300, source), Is.Zero, "the queries count one kind");

        // The rare code meets 51 other visits between two of its own: cold, at d0 32, by 51/83.
        IlLive.Rescore(rare, 700, source);
        double cold = 51.0 / (51 + 32);
        Assert.That(rare.Score, Is.EqualTo(1.1 * 10 * 20_000 - 10 * (25 + 10 * cold * 300)).Within(1e-9).Percent);
    }

    /// <summary>What the scorer should find for visits of <paramref name="ops"/> opcodes of loop code, <paramref name="visits"/> of them over <paramref name="epochs"/> epochs, on the queries' runner, as the only code counted.</summary>
    private static double LoopScore(int visits, int ops, int epochs, double a = 1.1)
    {
        IlModel model = IlModel.RpcPrior;
        double window = Enumerable.Range(0, epochs).Sum(i => Math.Pow(2, -i / 600.0));
        // The only code counted: one visit between two of its own, at d0 32.
        double cold = 1.0 / (1 + model.D0);
        return (a * ops * visits - visits * (model.G[0] + model.B[0] * cold * Math.Min(ops, model.U))) / window;
    }

    [Test]
    public void Scorer_golden()
    {
        // A heavy loop: ten visits of 20,000 opcodes a second for three seconds. Its score is the model's, and it is promoted.
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        byte[] heavyCode = Unique(CountLoopCode(), 0x20);
        IlCodeProfile heavy = ProfileOf(heavyCode);
        for (int epoch = 100; epoch < 103; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(heavy, 10, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(heavy.Score, Is.EqualTo(LoopScore(30, 20_000, 3)).Within(1e-9).Percent);
        Assert.That(heavy.Sigma, Is.EqualTo(heavy.Score / Math.Sqrt(30)).Within(1e-9).Percent, "no residual without a calibration");
        Assert.That(heavy.State, Is.EqualTo(IlCodeProfile.LiveQueued));
        Assert.That(IlLive.ListsForTests().Live, Is.EqualTo(new[] { heavy }));
        LayoutJob layout = (LayoutJob)_scheduler.PendingForTests().Single();
        Assert.That(layout.Density, Is.EqualTo(heavy.Density));
        Drain();
        IlEvm.IlContract live = heavy.Live!;
        Assert.That((heavy.State, live.Origin, live.RunMask, live.Profile), Is.EqualTo((IlCodeProfile.LivePromoted, IlOrigin.LiveRpc, IlRuntime.RunQuery, heavy)));
        Assert.That(live.Code.Span.SequenceEqual(heavyCode), Is.True, "laid out from the code database");
        Assert.That(IlLive.Live(block: false), Is.EqualTo((1L, live.MemoryBytes)), "the budget holds what the layout came to");

        // Light, frequent straight-line code: 10,000 visits of 300 opcodes a second on 10 KB, which the gate leaves to the
        // interpreter: no score. A loop of visits under the queries' admission length likewise, unless the gate is off.
        byte[] lightCode = Padded(CountLoopCode(), 0x21, 10_000), shortCode = Unique(CountLoopCode(), 0x22);
        IlCodeProfile light = ProfileOf(lightCode), shortLoop = ProfileOf(shortCode);
        for (int epoch = 103; epoch < 106; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(light, 10_000, ops: 300);
            Visits(shortLoop, 50, ops: 5_000);
        }

        IlLive.Tick(0);
        Assert.That((light.Score, shortLoop.Score), Is.EqualTo((0.0, 0.0)));
        Assert.That((light.State, shortLoop.State), Is.EqualTo((IlCodeProfile.LiveNone, IlCodeProfile.LiveNone)));
        Assert.That(LoopScore(10, 300, 1, a: 0.25), Is.LessThan(0), "straight-line code loses on every visit");
        IlEvmSettings.Gate = false;
        IlRuntime.ManualRpcEpoch = 106;
        Visits(shortLoop, 50, ops: 5_000);
        Visits(light, 10_000, ops: 300);
        IlLive.Tick(0);
        Assert.That(shortLoop.State, Is.EqualTo(IlCodeProfile.LiveQueued), "every visit runs compiled with the gate off");
        // The straight-line code's visits are admitted then, and lose: a bucket that loses counts for nothing, not less.
        Assert.That((light.Score, light.State), Is.EqualTo((0.0, IlCodeProfile.LiveNone)));
        IlEvmSettings.Gate = true;

        // Above the threshold but within twice its noise: 16 measured visits standing for 32 wait; 32 measured do not.
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        IlCodeProfile noisy = ProfileOf(Unique(CountLoopCode(), 0x23));
        for (int epoch = 100; epoch < 104; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(noisy, 4, ops: 10_000, weight: 2);
        }

        IlLive.Tick(0);
        Assert.That(noisy.Score, Is.GreaterThan(IlLive.Rpc.ThetaUp));
        Assert.That(noisy.Score - IlLive.Rpc.ThetaUp, Is.LessThan(2 * noisy.Sigma));
        Assert.That(noisy.State, Is.EqualTo(IlCodeProfile.LiveNone));
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        IlCodeProfile sure = ProfileOf(Unique(CountLoopCode(), 0x24));
        for (int epoch = 100; epoch < 104; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(sure, 8, ops: 10_000);
        }

        IlLive.Tick(0);
        Assert.That(sure.Score, Is.EqualTo(noisy.Score).Within(1e-9).Percent, "the same visits, measured more");
        Assert.That(sure.State, Is.EqualTo(IlCodeProfile.LiveQueued));

        // Busy enough, but counted in one epoch only, or on 15 measured visits: neither is promoted.
        IlCodeProfile burst = ProfileOf(Unique(CountLoopCode(), 0x25)), thin = ProfileOf(Unique(CountLoopCode(), 0x26));
        IlRuntime.ManualRpcEpoch = 104;
        Visits(burst, 400, ops: 20_000);
        Visits(thin, 8, ops: 20_000, weight: 16);
        IlRuntime.ManualRpcEpoch = 105;
        Visits(thin, 7, ops: 20_000, weight: 16);
        IlLive.Tick(0);
        Assert.That((burst.Epochs, burst.Score - IlLive.Rpc.ThetaUp > 2 * burst.Sigma, burst.State), Is.EqualTo((1, true, IlCodeProfile.LiveNone)), "one epoch");
        Assert.That((thin.Measures, thin.Score - IlLive.Rpc.ThetaUp > 2 * thin.Sigma, thin.State), Is.EqualTo((15.0, true, IlCodeProfile.LiveNone)), "15 measured");
    }

    [Test]
    public void Scorer_golden_blocks()
    {
        // Block processing's source: its own thread's visits, and the other threads' at rho; admitted from 2,048 opcodes,
        // or from a list entry's own minv.
        Fresh();
        Prefer(IlEvmPrefer.Blocks);
        IlCodeProfile profile = ProfileOf(Unique(CountLoopCode(), 0x27));
        for (int epoch = 1; epoch <= 8; epoch++)
        {
            IlRuntime.BlockEpoch = epoch;
            Visits(profile, 3, ops: 4_000, weight: 1, block: true, main: true);
            Visits(profile, 2, ops: 3_000, weight: 1, block: true, main: false);
            Visits(profile, 4, ops: 1_000, weight: 1, block: true, main: true);
        }

        IlLive.Tick(0);
        IlModel model = IlModel.BlocksPrior;
        double window = Enumerable.Range(0, 8).Sum(i => Math.Pow(2, -i / 512.0));
        // All 72 visits: 24 x 4,000 + 16 x 3,000 + 32 x 1,000 opcodes over 22 bytes, loops. Of the main kind's 56
        // visits, those of 1,000 opcodes are under the admission length; the other kind has 16.
        double coldMain = 56.0 / 56 / (56.0 / 56 + model.D0), coldOther = 16.0 / 16 / (16.0 / 16 + model.D0);
        double main = 1.1 * 24 * 4_000 - 24 * (model.G[0] + model.B[0] * coldMain * 300);
        double other = 1.1 * 16 * 3_000 - 16 * (model.G[1] + model.B[1] * coldOther * 300);
        Assert.That(profile.Score, Is.EqualTo((main + model.Rho * other) / window).Within(1e-9).Percent);
        Assert.That(profile.State, Is.EqualTo(IlCodeProfile.LiveQueued), "8 blocks, 72 measured visits, far over 5,000 ns a block");

        // A list entry's minv of 12 admits from 4,096 opcodes: nothing here.
        Fresh();
        Prefer(IlEvmPrefer.Blocks);
        IlCodeProfile strict = ProfileOf(Unique(CountLoopCode(), 0x2d));
        strict.BlockMinVisitLog2 = 12;
        for (int epoch = 1; epoch <= 8; epoch++)
        {
            IlRuntime.BlockEpoch = epoch;
            Visits(strict, 3, ops: 4_000, weight: 1, block: true);
        }

        IlLive.Tick(0);
        Assert.That((strict.Score, strict.State), Is.EqualTo((0.0, IlCodeProfile.LiveNone)));
        Assert.That(IlLive.AdmissionOps(strict, block: true), Is.EqualTo(4_096));
        Assert.That(IlLive.AdmissionOps(strict, block: false), Is.EqualTo(IlGate.Query.VisitColdOps), "the queries' runner has no list threshold");
    }

    [Test]
    public void Score_blends_the_check_s_measurement_and_the_model_s_residual()
    {
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        IlEvmSettings.SelfCheck = IlEvmSettings.QueryCheck = IlEvmSettings.BlockCheck = true;
        IlLive.Rpc = IlLiveParams.RpcPrior.With(model: IlModel.From(IlModel.RpcPrior, new Dictionary<string, string> { ["tau"] = "20000" }, ""));
        IlCodeProfile profile = ProfileOf(Unique(CountLoopCode(), 0x2f));
        for (int epoch = 100; epoch < 103; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(profile, 10, ops: 20_000);
        }

        // Before its check has timed it: the model's score, its noise with the residual whole.
        IlLive.Tick(0);
        double model = profile.Score;
        Assert.That(model, Is.EqualTo(LoopScore(30, 20_000, 3)).Within(1e-9).Percent);
        Assert.That(profile.Sigma, Is.EqualTo(Math.Sqrt(model * model / 30 + 20_000.0 * 20_000)).Within(1e-9).Percent);
        Drain();
        IlCheck check = profile.Live!.QueryCheck!;

        // Kept by its check at 0.8, seven pairs over three windows: the measurement weighs in by its pairs, and the
        // residual shrinks by them.
        foreach ((int window, int pairs) in new[] { (1, 3), (3, 3), (5, 1) })
        {
            for (int i = 0; i < IlCheck.RecentSamples; i++)
                check.AddCompiled(8 * 2_048, 2_048);
            for (int i = 0; i < pairs; i++)
                check.AddInterpreted(10 * 2_048, 2_048, window);
        }

        Assert.That(check.State, Is.EqualTo(IlCheck.Kept));
        (long checkPairs, double interpreted, double compiled) = check.Measured();
        Assert.That(checkPairs, Is.EqualTo(7));
        IlLive.Tick(0);
        double window3 = Enumerable.Range(0, 3).Sum(i => Math.Pow(2, -i / 600.0));
        double measured = 600_000 / window3 * (interpreted - compiled);
        double blended = (7 * measured + 16 * model) / (7 + 16);
        Assert.That(profile.Score, Is.EqualTo(blended).Within(1e-9).Percent);
        Assert.That(profile.Sigma, Is.EqualTo(Math.Sqrt(blended * blended / 30 + Math.Pow(20_000.0 * 16 / 23, 2))).Within(1e-9).Percent);
    }

    [Test]
    public void Calibration_sets_the_model_and_the_environment_the_thresholds()
    {
        string[] calibration = ["fit=ok a.loop=2.2 a.line.O=0.3 g.M=10 b=3 U=100 d0=8 rho=0.5 lambda=1000 tau=500", "rpc.b=7 rpc.lambda=2 theta=1 a.mixed=x"];
        IlLive.Calibrate(calibration);
        IlModel blocks = IlLive.Blocks.Model, rpc = IlLive.Rpc.Model;
        Assert.That(new[] { blocks.A(IlEvmCodeShape.Loop, 0), blocks.A(IlEvmCodeShape.Loop, 1), blocks.A(IlEvmCodeShape.Line, 0), blocks.A(IlEvmCodeShape.Line, 1), blocks.A(IlEvmCodeShape.Mixed, 0) },
            Is.EqualTo(new[] { 2.2, 2.2, 0.25, 0.3, 0.6 }), "a shape for both kinds, or for one; a value that is not a number is skipped");
        Assert.That(new[] { blocks.G[0], blocks.G[1], blocks.B[0], blocks.B[1], blocks.U, blocks.D0, blocks.Rho, blocks.Lambda, blocks.Tau },
            Is.EqualTo(new[] { 10, 25, 3, 3, 100, 8, 0.5, 1000, 500 }));
        Assert.That(new[] { rpc.B[0], rpc.Lambda, rpc.A(IlEvmCodeShape.Loop, 0), rpc.G[0], rpc.Rho }, Is.EqualTo(new[] { 7, 2, 1.1, 25, 0.25 }), "the queries' tokens are their own");
        Assert.That((IlLive.Blocks.ThetaUp, IlLive.Rpc.ThetaUp), Is.EqualTo((5_000.0, 50_000.0)), "the thresholds are not the list's");

        // The model's constants all at work: block processing's score of loops over 10 KB, both kinds, less lambda per MiB.
        Prefer(IlEvmPrefer.Blocks);
        IlCodeProfile profile = new(IlProfileKey.ByContent(5, 10_000), 10_000);
        for (int epoch = 1; epoch <= 4; epoch++)
        {
            IlRuntime.BlockEpoch = epoch;
            Visits(profile, 10, ops: 30_000, block: true, main: true);
            Visits(profile, 4, ops: 30_000, block: true, main: false);
        }

        IlLive.Fold(profile, 4, IlLive.Blocks);
        IlLive.Rescore(profile, 4, IlLive.Blocks);
        double cold = 1.0 / (1 + 8);
        double main = 2.2 * 40 * 30_000 - 40 * (10 + 3 * cold * 100), other = 2.2 * 16 * 30_000 - 16 * (25 + 3 * cold * 100);
        double window = Enumerable.Range(0, 4).Sum(i => Math.Pow(2, -i / 512.0));
        double score = (main + 0.5 * other) / window - 1_000 * 90.0 * 10_000 / (1 << 20);
        Assert.That(profile.Score, Is.EqualTo(score).Within(1e-9).Percent);
        Assert.That(profile.Sigma, Is.EqualTo(Math.Sqrt(score * score / 56 + 500.0 * 500)).Within(1e-9).Percent);
        Assert.That(profile.Density, Is.EqualTo(score / (90.0 * 10_000 / (1 << 20))).Within(1e-9).Percent);

        // The environment: the thresholds, and calibration tokens of its own, which win over the list's.
        string[] variables = ["NETHERMIND_ILEVM_LIVE_RPC_THETA_NS", "NETHERMIND_ILEVM_LIVE_BLOCKS_HALF_LIFE", "NETHERMIND_ILEVM_LIVE_RPC_MIN_MEASURED", "NETHERMIND_ILEVM_LIVE_CALIB"];
        string?[] saved = [.. variables.Select(Environment.GetEnvironmentVariable)];
        try
        {
            Environment.SetEnvironmentVariable(variables[0], "25000");
            Environment.SetEnvironmentVariable(variables[1], "256");
            Environment.SetEnvironmentVariable(variables[2], "4");
            Environment.SetEnvironmentVariable(variables[3], "rho=0.75 rpc.b=9");
            IlLive.Calibrate(calibration);
            Assert.That((IlLive.Rpc.ThetaUp, IlLive.Rpc.MinMeasured, IlLive.Blocks.HalfLife, IlLive.Blocks.CooldownEpochs, IlLive.Blocks.RestartEpochs),
                Is.EqualTo((25_000.0, 4, 256.0, 2_560L, 1_024)));
            Assert.That((IlLive.Blocks.Model.Rho, IlLive.Rpc.Model.B[0], IlLive.Blocks.Model.Lambda), Is.EqualTo((0.75, 9.0, 1_000.0)));
        }
        finally
        {
            for (int i = 0; i < variables.Length; i++)
                Environment.SetEnvironmentVariable(variables[i], saved[i]);
        }

        IlLive.Calibrate([]);
        Assert.That((IlLive.Blocks.Model.ToString(), IlLive.Rpc.Model.ToString()), Is.EqualTo((IlModel.BlocksPrior.ToString(), IlModel.RpcPrior.ToString())));
    }

    [Test]
    public void Module_sets_the_live_budget()
    {
        Assume.That(IlEvmModule.Unsupported(), Is.Null);
        ContainerBuilder builder = new();
        builder.RegisterModule(new IlEvmModule(new IlEvmConfig { Selection = IlEvmSelection.Adaptive, Enabled = true, LiveBudgetMiB = 64 }));
        using (builder.Build())
            Assert.That(IlLive.BudgetBytes, Is.EqualTo(64L << 20));
    }

    [Test]
    public void Scorer_demotes_after_three_low_ticks_and_keeps_a_cooldown()
    {
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        IlCodeProfile profile = ProfileOf(Unique(CountLoopCode(), 0x28));
        for (int epoch = 100; epoch < 103; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(profile, 6, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(profile.Score, Is.InRange(2 * IlLive.Rpc.ThetaUp, 3 * IlLive.Rpc.ThetaUp));
        Drain();
        IlEvm.IlContract live = profile.Live!;
        Assert.That(live, Is.Not.Null);

        // Ten seconds without its calls: under the threshold, but over a quarter of it, it stays, tick after tick.
        for (int epoch = 103; epoch <= 112; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            IlLive.Tick(0);
        }

        Assert.That(profile.Score, Is.InRange(IlLive.Rpc.ThetaUp / 4, IlLive.Rpc.ThetaUp));
        Assert.That((profile.Live, profile.BelowTicks), Is.EqualTo((live, 0)));

        // Its calls stop: an hour on, its score is under a quarter of the threshold; three ticks running demote it.
        IlRuntime.ManualRpcEpoch = 4_000;
        IlLive.Tick(0);
        IlLive.Tick(0);
        Assert.That((profile.Live, profile.BelowTicks, profile.Score < IlLive.Rpc.ThetaUp / 4), Is.EqualTo((live, 2, true)));
        int version = IlRuntime.Version;
        IlLive.Tick(0);
        Assert.That((profile.Live, profile.State, profile.CooldownUntil), Is.EqualTo(((IlEvm.IlContract?)null, IlCodeProfile.LiveDemoted, 4_000L + 6_000)));
        Assert.That(IlLive.ListsForTests().Live, Is.Empty);
        Assert.That(live.Runners.All(static r => r is null), Is.True, "its code released");
        Assert.That(IlRuntime.Version, Is.GreaterThan(version));
        Assert.That(IlLive.Live(block: false), Is.EqualTo((0L, 0L)));
        Assert.That(IlLive.Totals(), Is.EqualTo((1L, 1L, 1L, 0L, 0L)));

        // Its calls come back at the rate that promoted it: within the cooldown it waits; four times the threshold does not.
        for (int epoch = 4_001; epoch < 4_004; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(profile, 6, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(profile.Score, Is.InRange(IlLive.Rpc.ThetaUp, 4 * IlLive.Rpc.ThetaUp), "scored from its return, not diluted by its silence");
        Assert.That(profile.State, Is.EqualTo(IlCodeProfile.LiveDemoted), "in its cooldown");
        IlRuntime.ManualRpcEpoch = 4_004;
        Visits(profile, 200, ops: 20_000);
        IlLive.Tick(0);
        Assert.That(profile.Score, Is.GreaterThan(4 * IlLive.Rpc.ThetaUp));
        Assert.That(profile.State, Is.EqualTo(IlCodeProfile.LiveQueued), "promoted all the same");
        Drain();

        // A code very busy once, then silent for four half-lives, back at a rate under the threshold: what is left of its
        // busy time, a sixteenth, does not swell its new rate, which counts from its return.
        IlCodeProfile once = new(IlProfileKey.ByContent(77, 22), 22);
        for (int epoch = 5_000; epoch < 5_010; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(once, 1_000, ops: 20_000);
        }

        IlLive.Fold(once, 5_009, IlLive.Rpc);
        int back = 5_009 + IlLive.Rpc.RestartEpochs;
        for (int epoch = back; epoch < back + 8; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(once, 2, ops: 20_000);
        }

        IlLive.Fold(once, back + 7, IlLive.Rpc);
        IlLive.Rescore(once, back + 7, IlLive.Rpc);
        Assert.That((once.FirstEpoch, once.Epochs), Is.EqualTo((back, 8)));
        Assert.That(IlLive.Decayed(once, IlLive.BucketOf(20_000) * 2, back + 7, IlLive.Rpc), Is.EqualTo(16), "the history from its return");
        Assert.That(once.Score, Is.InRange(0.5 * IlLive.Rpc.ThetaUp, IlLive.Rpc.ThetaUp), "its new rate alone");

        // Another, demoted, back once its cooldown is over at the rate that promoted it: promoted again.
        IlCodeProfile other = ProfileOf(Unique(CountLoopCode(), 0x29));
        other.State = IlCodeProfile.LiveDemoted;
        other.CooldownUntil = 10_000;
        for (int epoch = 10_000; epoch < 10_003; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(other, 6, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(other.State, Is.EqualTo(IlCodeProfile.LiveQueued));
    }

    [Test]
    public void Scorer_follows_the_list_the_body_set_and_failed_layouts()
    {
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        void Hot(IlCodeProfile profile, int from)
        {
            for (int epoch = from; epoch < from + 3; epoch++)
            {
                IlRuntime.ManualRpcEpoch = epoch;
                Visits(profile, 10, ops: 20_000);
            }
        }

        // A code the code database lacks: its layout fails, counted, and the code waits a cooldown.
        byte[] missingCode = Unique(CountLoopCode(), 0x2a);
        IlCodeProfile missing = ProfileOf(missingCode);
        _codeDb.Remove(ValueKeccak.Compute(missingCode).Bytes);
        Hot(missing, 100);
        IlLive.Tick(0);
        Drain();
        Assert.That((missing.State, missing.Live, missing.CooldownUntil), Is.EqualTo((IlCodeProfile.LiveDemoted, (IlEvm.IlContract?)null, 102L + 6_000)));
        Assert.That((IlLive.Totals().Failed, IlLive.Live(block: false)), Is.EqualTo((1L, (0L, 0L))));

        // A live code the list then ships, as a leftover entry compiled between blocks: the shipped contract takes over.
        byte[] code = Unique(CountLoopCode(), 0x2b);
        IlCodeProfile profile = ProfileOf(code);
        Hot(profile, 103);
        IlLive.Tick(0);
        Drain();
        IlEvm.IlContract live = profile.Live!;
        Assert.That(live, Is.Not.Null);
        IlEvm.IlContract shipped = Ship(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(code)), code);
        Assert.That(shipped.Profile, Is.SameAs(profile));
        IlLive.Tick(0);
        Assert.That((profile.Live, profile.State, profile.Shipped), Is.EqualTo(((IlEvm.IlContract?)null, IlCodeProfile.LiveNone, shipped)));
        Assert.That(live.Runners.All(static r => r is null), Is.True);
        Hot(profile, 106);
        IlLive.Tick(0);
        Assert.That(profile.State, Is.EqualTo(IlCodeProfile.LiveNone), "never promoted beside its shipped contract");

        // A live code resolved on another body set: once, now and then, it keeps its contract; three ticks running, as
        // after a fork, it is released and promoted again for the new one.
        byte[] forkCode = Unique(CountLoopCode(), 0x2c);
        IlCodeProfile forked = ProfileOf(forkCode);
        Hot(forked, 109);
        IlLive.Tick(0);
        Drain();
        IlEvm.IlContract osaka = forked.Live!;
        OpcodeBodySet prague = Bodies(Nethermind.Specs.Forks.Prague.Instance);
        Assume.That(prague, Is.Not.SameAs(osaka.Bodies));
        forked.LastBodies = prague;
        IlLive.Tick(0);
        forked.LastBodies = osaka.Bodies;
        IlLive.Tick(0);
        forked.LastBodies = prague;
        IlLive.Tick(0);
        IlLive.Tick(0);
        Assert.That(forked.Live, Is.SameAs(osaka));
        IlLive.Tick(0);
        Assert.That((forked.Live, osaka.Runners.All(static r => r is null)), Is.EqualTo(((IlEvm.IlContract?)null, true)));
        Hot(forked, 112);
        IlLive.Tick(0);
        Drain();
        Assert.That(forked.Live?.Bodies, Is.SameAs(prague));
        Assert.That(IlLive.Describe(), Does.Contain(", 1 moved by a fork; "));
    }

    [Test]
    public void A_live_contract_follows_the_fork_its_calls_run_on()
    {
        // Calls on Osaka promote a contract compiled for Osaka; then the calls run on Prague, as after a fork. Their parts
        // miss its body set, which tells the profile, with no state resolving again; three ticks on it is released, and
        // promoted again for Prague, whose calls then run compiled.
        OpcodeBodySet osaka = Bodies(Spec), prague = Bodies(Nethermind.Specs.Forks.Prague.Instance);
        Assume.That(prague, Is.Not.SameAs(osaka), "Prague and Osaka must run different bodies for this test");
        IlEvmSettings.ChunkThreshold = 1;
        Fresh(scorer: true);
        Prefer(IlEvmPrefer.Rpc);
        IlEvmSettings.Gate = false;
        IlGate.QuerySampleEvery = 1;
        byte[] code = Unique(CountLoopCode(), 0x43);
        _codeDb[ValueKeccak.Compute(code).Bytes] = code;
        CodeInfo codeInfo = Stamped(code);
        byte[] input = Word(2_000);
        Outcome onOsaka = Interpret(code, input), onPrague = Interpret(code, input, vm: PragueQueryVm);

        // A second of calls on one machine, then the second's end: idle, with no block, the scorer ticks once a second.
        void Second(EthereumVirtualMachine vm, int epoch)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            for (int call = 0; call < 16; call++)
                Assert.That(Run(vm, codeInfo, input), Is.EqualTo(ReferenceEquals(vm, QueryVm) ? onOsaka : onPrague));
            PumpUntil(_time.Ms + 1_000);
        }

        for (int epoch = 100; epoch < 103; epoch++)
            Second(QueryVm, epoch);
        IlCodeProfile profile = StateOf(codeInfo).Profile!;
        IlEvm.IlContract first = profile.Live!;
        Assert.That(first?.Bodies, Is.SameAs(osaka), "promoted for the body set its calls ran on");

        // The fork: its calls miss the contract's body set, once per state, and the profile follows them.
        long misses = IlRuntime.ForkMisses;
        Second(PragueQueryVm, 103);
        Assert.That((profile.LastBodies, IlRuntime.ForkMisses - misses), Is.EqualTo((prague, 1L)));
        Assert.That(profile.Live, Is.SameAs(first), "once, it keeps its contract");
        Second(PragueQueryVm, 104);
        Second(PragueQueryVm, 105);
        Assert.That((first.IsReleased, profile.Live is null || !ReferenceEquals(profile.Live, first)), Is.EqualTo((true, true)), "three ticks running: released");
        Second(PragueQueryVm, 106);
        Second(PragueQueryVm, 107);
        IlEvm.IlContract second = profile.Live!;
        Assert.That(second?.Bodies, Is.SameAs(prague), "promoted again, for the fork's body set");
        // Its calls reach its chunk, which compiles between them.
        Second(PragueQueryVm, 108);
        Assert.That(second!.Runners.All(static r => r is not null), Is.True);
        RunCounter runs = CountRuns(second);
        Assert.That(Run(PragueQueryVm, codeInfo, input), Is.EqualTo(onPrague));
        Assert.That(runs.Runs, Is.EqualTo(1), "the fork's calls run compiled");
        Assert.That(IlLive.Describe(), Does.Contain(", 1 moved by a fork; "));
    }

    [Test]
    public void The_adaptive_rules_never_jit_on_a_frame_s_thread_whatever_the_knobs_say()
    {
        // Eager chunks: a promotion's layout publishes its contract with every chunk a job of its own, never one long step.
        IlEvmSettings.EagerChunks = true;
        IlEvmSettings.CompileSynchronously = true;
        IlEvmSettings.ChunkThreshold = 1;
        Fresh();
        IlEvm.IlContract Promoted(byte[] code)
        {
            IlEvm.IlContract? laidOut = null;
            Assert.That(_scheduler.Enqueue(new LayoutJob(() => code, Bodies(Spec), (contract, _) =>
            {
                laidOut = contract;
                IlCodeProfile profile = ProfileOf(code);
                contract!.Profile = profile;
                contract.Origin = IlOrigin.LiveRpc;
                contract.RunMask = IlRuntime.RunQuery;
                profile.Live = contract;
                IlRuntime.Grew(block: false, query: true);
            })), Is.True);
            Assert.That((_scheduler.Pump(0), _scheduler.Pump(0)), Is.EqualTo((true, true)), "read, then laid out and published");
            return laidOut!;
        }

        byte[] eager = Unique(CountLoopCode(), 0x44);
        IlEvm.IlContract contract = Promoted(eager);
        Assert.That(contract.Runners.All(static r => r is null), Is.True, "no chunk compiled in the layout's step");
        Assert.That(_scheduler.PendingForTests().Select(static j => ((ChunkJob)j).Chunk), Is.EquivalentTo(Enumerable.Range(0, contract.ChunkCount)));
        Drain();
        Assert.That(contract.Runners.All(static r => r is not null), Is.True);

        // Compiling synchronously: a frame that reaches a cold chunk hands back and queues its JIT, as with the knob off.
        // The gate off, so the frame runs compiled code.
        IlEvmSettings.EagerChunks = false;
        IlEvmSettings.Gate = false;
        byte[] cold = Unique(CountLoopCode(), 0x45);
        IlEvm.IlContract lazy = Promoted(cold);
        CodeInfo codeInfo = Stamped(cold);
        byte[] input = Word(10);
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(Interpret(cold, input)));
        Assert.That(lazy.Runners[lazy.EntryChunk[0]], Is.Null, "nothing compiled on the frame's thread");
        Assert.That(_scheduler.PendingForTests().Single(), Is.InstanceOf<ChunkJob>());
    }

    [Test]
    public void Budget_and_caps()
    {
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        // Four loops of 4 KB, the denser the busier; the budget holds two and a half of their estimates.
        IlCodeProfile[] loops = [.. Enumerable.Range(0, 4).Select(i => ProfileOf(Padded(CountLoopCode(), (byte)(0x30 + i), 4_096)))];
        long estimate = IlLive.EstimateBytes(4_096);
        IlLive.BudgetBytes = 2 * estimate + estimate / 2;
        for (int epoch = 100; epoch < 103; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            for (int i = 0; i < loops.Length; i++)
                Visits(loops[i], 40 - 5 * i, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(loops.Select(static p => p.Density), Is.Ordered.Descending);
        Assert.That(IlLive.ListsForTests().Live, Is.EqualTo(loops[..2]), "the densest, while the budget allows");
        Assert.That(IlLive.ListsForTests().Candidates, Is.EquivalentTo(loops[2..]), "the rest wait");
        Assert.That(IlLive.Live(block: false).Bytes, Is.EqualTo(2 * estimate), "estimated until laid out");
        Drain();
        long held = IlLive.Live(block: false).Bytes;
        Assert.That(held, Is.EqualTo(loops[0].Live!.MemoryBytes + loops[1].Live!.MemoryBytes));

        // The budget full: a candidate at least twice as dense as the least dense live code takes its place; the
        // others, less dense than that, still wait.
        IlLive.BudgetBytes = held;
        IlCodeProfile dense = ProfileOf(Padded(CountLoopCode(), 0x34, 600));
        long denseEstimate = IlLive.EstimateBytes(600);
        Assume.That(denseEstimate, Is.LessThanOrEqualTo(loops[1].Live!.MemoryBytes), "one release makes room");
        for (int epoch = 103; epoch < 106; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(dense, 40, ops: 20_000);
            foreach (IlCodeProfile loop in loops)
                Visits(loop, 1, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(dense.Density, Is.GreaterThanOrEqualTo(2 * loops[1].Density));
        Assert.That(loops[2].Density, Is.LessThan(2 * loops[1].Density));
        Assert.That((loops[1].State, loops[1].Live), Is.EqualTo((IlCodeProfile.LiveNone, (IlEvm.IlContract?)null)), "the least dense let go of, with no cooldown");
        Assert.That((IlLive.Displacements, IlLive.Totals().Demoted), Is.EqualTo((1L, 0L)), "displaced for room, not demoted");
        Assert.That((loops[0].State, dense.State), Is.EqualTo((IlCodeProfile.LivePromoted, IlCodeProfile.LiveQueued)), "one release made room");
        Assert.That((loops[2].State, loops[3].State), Is.EqualTo((IlCodeProfile.LiveNone, IlCodeProfile.LiveNone)));
        Drain();
        Assert.That(IlLive.Live(block: false).Bytes, Is.LessThanOrEqualTo(IlLive.BudgetBytes));

        // A candidate denser than every live code, but whose estimate no release could make room for: none is released.
        IlCodeProfile big = ProfileOf(Padded(CountLoopCode(), 0x35, 4_096));
        for (int epoch = 106; epoch < 109; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(big, 2_000, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(big.Density, Is.GreaterThan(2 * Math.Max(dense.Density, loops[0].Density)));
        Assert.That((big.State, loops[0].State, dense.State), Is.EqualTo((IlCodeProfile.LiveNone, IlCodeProfile.LivePromoted, IlCodeProfile.LivePromoted)));

        // Codes sized so that releasing the least dense live code alone is not room enough, and releasing both is. One
        // at least twice as dense as the least dense but not as the other displaces neither; one twice as dense as both
        // displaces both.
        long free = IlLive.BudgetBytes - IlLive.Live(block: false).Bytes, least = loops[0].Live!.MemoryBytes, other = dense.Live!.MemoryBytes;
        int size = (int)((free + least + other / 2) / IlLive.IlBytesPerCodeByte);
        Assume.That(IlLive.EstimateBytes(size), Is.InRange(free + least + 1, free + least + other - 1));
        IlCodeProfile middling = ProfileOf(Padded(CountLoopCode(), 0x36, size)), densest = ProfileOf(Padded(CountLoopCode(), 0x37, size));
        // The live codes keep their calls meanwhile, so their densities hold.
        void Busy(int epoch)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(loops[0], 40, ops: 20_000);
            Visits(dense, 40, ops: 20_000);
        }

        for (int epoch = 109; epoch < 112; epoch++)
        {
            Busy(epoch);
            Visits(middling, 50, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(middling.Density, Is.InRange(2 * loops[0].Density, 2 * dense.Density));
        Assert.That((middling.State, loops[0].State, dense.State), Is.EqualTo((IlCodeProfile.LiveNone, IlCodeProfile.LivePromoted, IlCodeProfile.LivePromoted)), "none released in vain");
        for (int epoch = 112; epoch < 115; epoch++)
        {
            Busy(epoch);
            Visits(densest, 600, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(densest.Density, Is.GreaterThan(2 * dense.Density));
        Assert.That((densest.State, loops[0].State, dense.State), Is.EqualTo((IlCodeProfile.LiveQueued, IlCodeProfile.LiveNone, IlCodeProfile.LiveNone)), "both released for it");
        Assert.That((IlLive.Displacements, IlLive.Totals().Demoted), Is.EqualTo((3L, 0L)));
        Assert.That((big.State, middling.State), Is.EqualTo((IlCodeProfile.LiveNone, IlCodeProfile.LiveNone)));
        Drain();
        Assert.That(IlLive.Live(block: false), Is.EqualTo((1L, densest.Live!.MemoryBytes)));

        // A promotion whose layout still waits, as when its window closed, is never a release's victim: it holds no code
        // yet. Here one is queued, and in the next tick a far denser candidate takes the place of the live code alone.
        IlLive.BudgetBytes = IlLive.Live(block: false).Bytes + IlLive.EstimateBytes(600);
        IlCodeProfile waiting = ProfileOf(Padded(CountLoopCode(), 0x38, 600)), rival = ProfileOf(Padded(CountLoopCode(), 0x39, 300));
        Assume.That(IlLive.EstimateBytes(300), Is.LessThanOrEqualTo(densest.Live!.MemoryBytes));
        for (int epoch = 115; epoch < 118; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(densest, 600, ops: 20_000);
            Visits(waiting, 40, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(waiting.State, Is.EqualTo(IlCodeProfile.LiveQueued));
        for (int epoch = 118; epoch < 121; epoch++)
        {
            IlRuntime.ManualRpcEpoch = epoch;
            Visits(densest, 600, ops: 20_000);
            Visits(rival, 1_000, ops: 20_000);
        }

        IlLive.Tick(0);
        Assert.That(waiting.Density, Is.LessThan(densest.Density));
        Assert.That(rival.Density, Is.GreaterThan(2 * densest.Density));
        Assert.That((waiting.State, rival.State, densest.State), Is.EqualTo((IlCodeProfile.LiveQueued, IlCodeProfile.LiveQueued, IlCodeProfile.LiveNone)));
        Drain();
        Assert.That(waiting.Live, Is.Not.Null);
        Assert.That(rival.Live, Is.Not.Null);
    }

    [Test]
    public void Profile_table_and_retained_bytes_stay_under_their_caps()
    {
        // 50,000 distinct codes no cache stamped, as state overrides send them, half a KB each, every transaction sampled:
        // the table stops at its cap, and the bytes the profiles keep at theirs. One code in 8 is called twice.
        Fresh();
        Prefer(IlEvmPrefer.Rpc);
        IlGate.QuerySampleEvery = 1;
        Assume.That((IlRuntime.MaxProfiles, IlRuntime.MaxRetainedBytes), Is.EqualTo((32_768, 16L << 20)));
        byte[] input = [];
        List<CodeInfo> busier = [];
        for (int i = 0; i < 50_000; i++)
        {
            // STOP, then the code's own number in bytes no frame reaches.
            byte[] code = new byte[520];
            BitConverter.TryWriteBytes(code.AsSpan(1), i);
            CodeInfo codeInfo = new(code);
            Run(QueryVm, codeInfo, input);
            if (i % 8 == 0 && i < IlRuntime.MaxProfiles)
            {
                Run(QueryVm, codeInfo, input);
                busier.Add(codeInfo);
            }
        }

        Assert.That(IlRuntime.ProfileCount, Is.EqualTo(IlRuntime.MaxProfiles));
        Assert.That(IlRuntime.RetainedBytes, Is.InRange((16L << 20) - 520, 16L << 20));
        // The thread's latest visits are counted once their ways are taken; all of them now.
        IlThread.Current.FoldAll();
        Assert.That(IlLive.Touched.Count, Is.EqualTo(IlRuntime.MaxProfiles), "each profile counted, and touched once");
        CodeInfo overCap = new(new byte[] { 0x00, 0xfe, 0xfe });
        Run(QueryVm, overCap, input);
        Assert.That(IlEvmFrameRunner.CodeSlot(overCap), Is.InstanceOf<IlQuiet>(), "past the cap, code is not profiled, and is declined from its mark");

        // Untouched for 1,024 epochs: the scorer's eviction lets go of the least worth keeping, down to seven eighths of
        // the cap, and their retained bytes with them; new code is profiled again.
        long retained = IlRuntime.RetainedBytes;
        IlRuntime.ManualRpcEpoch = 100 + IlLive.UntouchedEpochs - 1;
        IlLive.Tick(0);
        Assert.That((IlRuntime.ProfileCount, IlLive.Totals().Evicted), Is.EqualTo((IlRuntime.MaxProfiles, 0L)), "touched within 1,024 epochs");
        IlRuntime.ManualRpcEpoch = 100 + IlLive.UntouchedEpochs;
        IlLive.Tick(0);
        Assert.That(IlRuntime.ProfileCount, Is.EqualTo(IlRuntime.MaxProfiles * 7 / 8));
        Assert.That(busier.All(static c => StateOf(c).Profile is { State: not IlCodeProfile.LiveEvicted }), Is.True, "the busier, with more opcodes, are kept");
        Assert.That(IlLive.Totals().Evicted, Is.EqualTo(IlRuntime.MaxProfiles / 8));
        Assert.That(IlRuntime.RetainedBytes, Is.LessThan(retained));
        CodeInfo after = new(new byte[] { 0x00, 0xfd, 0xfd });
        Run(QueryVm, after, input);
        Assert.That(StateOf(after).Profile, Is.Not.Null);
    }

    [Test]
    public void Prefer_rpc_promotes_a_contract_hot_in_calls_in_a_window_and_prefer_blocks_never_does()
    {
        IlEvmSettings.ChunkThreshold = 1;
        byte[] code = Unique(CountLoopCode(), 0x40);
        byte[] input = Word(2_000);
        Outcome expected = Interpret(code, input);
        Assert.That(expected.OpCodes, Is.EqualTo(20_010));

        // Prefer=Rpc: calls over three seconds, the code on the interpreter, one visit in 8 measured.
        Fresh(scorer: true);
        Prefer(IlEvmPrefer.Rpc);
        _codeDb[ValueKeccak.Compute(code).Bytes] = code;
        CodeInfo codeInfo = Stamped(code);
        for (int second = 0; second < 3; second++)
        {
            IlRuntime.ManualRpcEpoch = 100 + second;
            for (int call = 0; call < 96; call++)
                Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        }

        IlCodeProfile profile = StateOf(codeInfo).Profile!;
        Assert.That((profile.Live, StateOf(codeInfo).QueryRun), Is.EqualTo(((IlEvm.IlContract?)null, (IlEvm.IlContract?)null)));

        // A block comes: nothing is decided while it is processed, nor in the quiet time after it; the window's opening
        // scores, and the promotion's layout runs in the window.
        Block[] blocks = Branch(TipTimestamp);
        Begin(blocks);
        Assert.That(_scheduler.Pump(0), Is.False);
        _time.Advance(30);
        Complete(blocks);
        PumpUntil(_time.Ms + 240);
        Assert.That((IlLive.Totals().Promoted, _scheduler.Steps, _scheduler.Windows), Is.EqualTo((0L, 0L, 0L)));
        PumpUntil(_time.Ms + 20);
        IlEvm.IlContract live = profile.Live!;
        Assert.That(live, Is.Not.Null, "promoted in the window");
        Assert.That((_scheduler.Windows, IlLive.Totals().Promoted, live.Origin, live.RunMask), Is.EqualTo((1L, 1L, IlOrigin.LiveRpc, IlRuntime.RunQuery)));

        // Calls run it: the first reaches its entry chunk cold and queues its JIT, which the window compiles; then they run
        // compiled, from their first part, as the gate learned their length while sampling.
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(StateOf(codeInfo).QueryRun, Is.SameAs(live));
        PumpUntil(_time.Ms + 50);
        Assert.That(live.Runners[live.EntryChunk[0]], Is.Not.Null);
        Assert.That(_scheduler.JitMs, Is.GreaterThan(0), "the chunk's JIT, in the window");
        RunCounter runs = CountRuns(live);
        Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1), "runs compiled");
        Assert.That(IlThread.Current.VisitOf(StateOf(codeInfo))!.Value.Mode, Is.EqualTo(IlGate.ModeCompiled));

        // Block processing never runs what the queries promoted.
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.EqualTo(1));
        Assert.That(IlLive.Describe(), Does.StartWith("live rpc: 1 promoted, 0 demoted, 0 displaced for room, 0 released, 0 layouts failed, 0 deferred, 0 moved by a fork; 1 live in "));
        Assert.That(_scheduler.Describe(), Does.Contain(" s of JIT in the steps; "));

        // Prefer=Blocks: the same calls are counted nowhere, and nothing is promoted.
        Fresh(scorer: true);
        Prefer(IlEvmPrefer.Blocks);
        _codeDb[ValueKeccak.Compute(code).Bytes] = code;
        CodeInfo again = Stamped(code);
        for (int second = 0; second < 3; second++)
        {
            IlRuntime.ManualRpcEpoch = 100 + second;
            for (int call = 0; call < 96; call++)
                Assert.That(Run(QueryVm, again, input), Is.EqualTo(expected));
        }

        Begin(blocks = Branch(TipTimestamp));
        _time.Advance(30);
        Complete(blocks);
        PumpUntil(_time.Ms + 1_000);
        Assert.That((_scheduler.Windows, IlLive.Totals().Promoted, IlRuntime.ProfileCount), Is.EqualTo((1L, 0L, 0)));
        Assert.That(IlEvmFrameRunner.CodeSlot(again), Is.SameAs(IlRuntime.QuietAll), "declined from its mark, uncounted");
        Assert.That(IlLive.Describe(), Does.StartWith("live blocks: 0 promoted"));
    }

    [Test]
    public void Override_code_hot_in_calls_is_promoted_from_its_retained_bytes()
    {
        // Code no cache stamped, as a state override sends it, one code info for the calls as the override cache hands it
        // them: keyed by its content, its bytes kept, laid out from them, and attached to a new code info of it, as after
        // the cache let it go, once its bytes compare equal.
        IlEvmSettings.EagerChunks = true;
        Fresh(scorer: true);
        Prefer(IlEvmPrefer.Rpc);
        byte[] code = Unique(CountLoopCode(), 0x41);
        byte[] input = Word(2_000);
        Outcome expected = Interpret(code, input);
        CodeInfo overridden = new(code);
        for (int second = 0; second < 3; second++)
        {
            IlRuntime.ManualRpcEpoch = 100 + second;
            for (int call = 0; call < 96; call++)
                Assert.That(Run(QueryVm, overridden, input), Is.EqualTo(expected));
        }

        // Idle, with no block since startup: the maintenance scores at once, and the layout runs.
        Drain();
        IlCodeProfile profile = IlRuntime.Profiles.Values.Single(static p => p.Key.IsByContent && p.Live is not null);
        Assert.That(profile.Retained.Span.SequenceEqual(code), Is.True);
        Assert.That(profile.Live!.Code.Span.SequenceEqual(code), Is.True);
        CodeInfo next = new(code);
        RunCounter runs = CountRuns(profile.Live);
        Assert.That(Run(QueryVm, next, input), Is.EqualTo(expected));
        Assert.That((StateOf(next).QueryRun, runs.Runs), Is.EqualTo((profile.Live, 1)));
        // Other bytes of the same length are another code, profiled apart, with nothing to run.
        byte[] other = [.. code];
        other[^1] ^= 0xff;
        CodeInfo otherInfo = new(other);
        Assert.That(Run(QueryVm, otherInfo, input), Is.EqualTo(Interpret(other, input)));
        Assert.That((IlEvmFrameRunner.CodeSlot(otherInfo) as IlEvm.IlCodeState)?.QueryRun, Is.Null, "a mark, or a state for its counts in a sampled transaction");
    }

    [Test]
    public void Prefer_blocks_promotes_a_contract_hot_in_blocks_which_both_runners_run()
    {
        IlEvmSettings.ChunkThreshold = 1;
        Fresh(scorer: true);
        Prefer(IlEvmPrefer.Blocks);
        byte[] code = Unique(CountLoopCode(), 0x42);
        byte[] input = Word(2_000);
        Outcome expected = Interpret(code, input);
        _codeDb[ValueKeccak.Compute(code).Bytes] = code;
        CodeInfo codeInfo = Stamped(code);
        // Another code info of the code, met between branches, as the mempool's session meets it: marked.
        CodeInfo marked = Stamped(code);
        Assert.That(Run(BlockVm, marked, input), Is.EqualTo(expected));
        Assert.That(IlEvmFrameRunner.CodeSlot(marked), Is.SameAs(IlRuntime.QuietAll));

        // Blocks at the tip a slot apart, each with 64 visits on block processing's thread, one in 16 measured: promoted
        // once it has 8 blocks and 32 measured visits, in the window after a block.
        IlEvm.IlContract? live = null;
        int blocksSeen = 0;
        for (; blocksSeen < 20 && live is null; blocksSeen++)
        {
            Block[] blocks = Branch(TipTimestamp);
            Begin(blocks);
            for (int visit = 0; visit < 64; visit++)
                Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
            _time.Advance(30);
            Complete(blocks);
            PumpUntil(_time.Ms + 1_000);
            live = StateOf(codeInfo).Profile?.Live;
            _time.Advance(12_000 - 1_030);
        }

        Assert.That(live, Is.Not.Null);
        Assert.That(blocksSeen, Is.InRange(IlLive.Blocks.MinEpochs, 12));
        Assert.That((live!.Origin, live.RunMask), Is.EqualTo((IlOrigin.LiveBlocks, IlRuntime.RunAll)));

        // Both runners run it: block processing in its next branch, once its chunk is compiled in a window, and the queries.
        Block[] next = Branch(TipTimestamp);
        Begin(next);
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
        _time.Advance(30);
        Complete(next);
        PumpUntil(_time.Ms + 1_000);
        RunCounter runs = CountRuns(live);
        Begin(next = Branch(TipTimestamp));
        Assert.That(OnBlockThread(() => Run(BlockVm, codeInfo, input)), Is.EqualTo(expected));
        Complete(next);
        Assert.That(runs.Runs, Is.EqualTo(1), "compiled in block processing");
        // The queries' runner runs it too, once its gate admits the visits: warm from block processing's run on this
        // thread, or after the visits it learned of its own, three at most, rent on the interpreter.
        for (int call = 0; call < IlGate.Query.MinVisits + 1; call++)
            Assert.That(Run(QueryVm, codeInfo, input), Is.EqualTo(expected));
        Assert.That(StateOf(codeInfo).QueryRun, Is.SameAs(live));
        Assert.That(runs.Runs, Is.InRange(2, 2 + IlGate.Query.MinVisits), "and in the queries");
        // The code info marked before: block processing's marks were replaced, so it resolves, and runs compiled too.
        int before = runs.Runs;
        Assert.That(OnBlockThread(() => Run(BlockVm, marked, input)), Is.EqualTo(expected));
        Assert.That((StateOf(marked).BlockRun, runs.Runs), Is.EqualTo((live, before + 1)));
        Assert.That(IlLive.Describe(), Does.StartWith("live blocks: 1 promoted"));
    }
}
