// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.IlEvm.Test;

/// <summary>
/// The inter-block scheduler on a clock moved by hand, its workers pumped by the test: the quiet time, windows and their
/// guard times at the tip and off it, back-to-back blocks, missed slots, syncing, idleness and its workers, steps that
/// resume across windows or run into a block, the estimates, the maintenance, and the jobs the rules route to it.
/// </summary>
[NonParallelizable]
public class IlEvmSchedulerTests : IlEvmAdaptiveFixture
{
    /// <summary>Old blocks' timestamps, as a benchmark replays them: far off the wall clock, so off the chain's tip.</summary>
    private const ulong Historic = 1_600_000_000;

    /// <summary>A clock tests move by hand, in milliseconds; the wall clock moves with it.</summary>
    private sealed class ManualTime : TimeProvider
    {
        /// <summary>The scheduler's clock, in milliseconds.</summary>
        public long Ms = 1_000_000;

        /// <summary>The wall clock, Unix milliseconds, when <see cref="Ms"/> is 0: a whole second at the start.</summary>
        public long UnixAtZero = 1_800_000_000_000 - 1_000_000;

        public long UnixMs => UnixAtZero + Ms;

        public override long TimestampFrequency => 1_000;

        public override long GetTimestamp() => Ms;

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeMilliseconds(UnixMs);

        public void Advance(long ms) => Ms += ms;
    }

    /// <summary>A job of <paramref name="steps"/> steps of <paramref name="units"/> each, every step moving the clock <paramref name="stepMs"/>; it notes when each started.</summary>
    private sealed class StepsJob(ManualTime time, int steps, double units = 1, long stepMs = 1, IlStepKind kind = IlStepKind.Light,
        int jobClass = IlJob.ClassChunk, double density = 0, Action<int>? during = null) : IlJob(jobClass, density)
    {
        public readonly List<long> Ran = [];
        public int Done;

        public override (IlStepKind Kind, double Units) Next => (kind, units);

        public override bool Step()
        {
            Ran.Add(time.Ms);
            during?.Invoke(Done);
            time.Advance(stepMs);
            return ++Done == steps;
        }
    }

    private ManualTime _time = null!;
    private IBranchProcessor _branch = null!;
    private IBlockTree _tree = null!;
    private IChainHeadInfoProvider _head = null!;
    private IlScheduler _scheduler = null!;
    private ulong _number = 100;

    [SetUp]
    public void Scheduling()
    {
        IlEvmSettings.EagerChunks = false;
        IlEvmSettings.CompileSynchronously = false;
        IlCheckClock.ManualTicks = true;
    }

    /// <summary>
    /// A scheduler on a fresh hand-moved clock, attached to substitutes of block processing, the block tree and the chain
    /// head, and, unless <paramref name="start"/> is false, started: idle, as after a startup compile.
    /// </summary>
    private IlScheduler Make(int compileThreads = 1, int idleThreads = 2, ulong secondsPerSlot = 12, bool start = true)
    {
        _time = new ManualTime();
        _branch = Substitute.For<IBranchProcessor>();
        _tree = Substitute.For<IBlockTree>();
        _head = Substitute.For<IChainHeadInfoProvider>();
        _head.IsSyncing.Returns(false);
        _queue = Substitute.For<IBlockProcessingQueue>();
        _scheduler = IlScheduler.Attach(new IlSchedulerOptions(250, 300, compileThreads, idleThreads, secondsPerSlot), _branch, _tree, _head, null, null,
            time: _time, threads: false, processingQueue: _queue);
        if (start)
            _scheduler.Start();
        return _scheduler;
    }

    private IBlockProcessingQueue _queue = null!;

    /// <summary>The processing queue takes in <paramref name="block"/>, before its senders are recovered and its branch built.</summary>
    private void Arrive(Block block) => _queue.BlockAdded += Raise.EventWith(_queue, new BlockEventArgs(block));

    /// <summary>The processing queue lets go of <paramref name="block"/>, processed or dropped.</summary>
    private void Remove(Block block) => _queue.BlockRemoved += Raise.EventWith(_queue, new BlockRemovedEventArgs(block.Hash!, ProcessingResult.Success));

    private T Queue<T>(T job) where T : IlJob
    {
        Assert.That(_scheduler.Enqueue(job), Is.True);
        return job;
    }

    /// <summary>The head's timestamp of a block at the chain's tip: the wall clock's second now.</summary>
    private ulong TipTimestamp => (ulong)(_time.UnixMs / 1000);

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

    /// <summary>A branch of <paramref name="count"/> blocks, the last at <paramref name="timestamp"/>, processed in <paramref name="processingMs"/>.</summary>
    private Block[] Process(ulong timestamp, long processingMs = 30, int count = 1)
    {
        Block[] blocks = Branch(timestamp, count);
        Begin(blocks);
        _time.Advance(processingMs);
        Complete(blocks);
        return blocks;
    }

    /// <summary>
    /// Moves the clock to <paramref name="until"/>, pumping <paramref name="worker"/> as its thread would: again at once
    /// after a step, which moves the clock itself, else after <paramref name="every"/> ms; and once more at the end.
    /// </summary>
    private void PumpUntil(long until, long every = 10, int worker = 0)
    {
        while (_time.Ms < until)
        {
            if (!_scheduler.Pump(worker))
                _time.Advance(Math.Max(0, Math.Min(every, until - _time.Ms)));
        }

        _scheduler.Pump(worker);
    }

    /// <summary>Pumps <paramref name="worker"/> until it starts no step; how many it ran.</summary>
    private int Drain(int worker = 0)
    {
        int steps = 0;
        while (_scheduler.Pump(worker))
            steps++;
        return steps;
    }

    /// <summary>Six old blocks a second apart, each processed in 30 ms; the last one's arrival, after which the gaps set its guard time.</summary>
    private long SecondCadence(int blocks = 6)
    {
        long arrival = 0;
        for (int i = 0; i < blocks; i++)
        {
            if (i > 0)
                _time.Advance(1_000 - 30);
            arrival = _time.Ms;
            Process(Historic + _number);
        }

        return arrival;
    }

    [Test]
    public void Quiet_then_open()
    {
        Make();
        Block[] blocks = Branch(TipTimestamp);
        Begin(blocks);
        StepsJob job = Queue(new StepsJob(_time, steps: 3));
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Busy));
        _time.Advance(30);
        Assert.That(_scheduler.Pump(0), Is.False, "no step starts while block processing runs");
        Complete(blocks);
        long completed = _time.Ms;
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Quiet));
        _time.Advance(249);
        Assert.That((_scheduler.Pump(0), _scheduler.State), Is.EqualTo((false, IlSchedulerState.Quiet)), "nor in the quiet time");
        _time.Advance(1);
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((_scheduler.State, _scheduler.Windows, job.Ran[0]), Is.EqualTo((IlSchedulerState.Open, 1L, completed + 250)));

        // A new head 100 ms after the next branch's completion starts the quiet time again: the window opens 350 ms after the completion.
        blocks = Process(TipTimestamp);
        completed = _time.Ms;
        _time.Advance(100);
        _tree.NewHeadBlock += Raise.EventWith(_tree, new BlockEventArgs(blocks[0]));
        _time.Advance(249);
        Assert.That(_scheduler.Pump(0), Is.False);
        _time.Advance(1);
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((job.Ran[1], _scheduler.Windows), Is.EqualTo((completed + 350, 2L)));
    }

    [Test]
    public void Guard_1s_cadence()
    {
        Make();
        // Off the tip the gaps set the guard time: the 10th percentile gap after the arrival, less 300 ms.
        long arrival = SecondCadence();
        (long guard, long missed) = _scheduler.SlotForTests();
        Assert.That((guard, missed), Is.EqualTo((arrival + 700, arrival + 2_000)));

        // Steps of 150 ms: one starts as the window opens, 280 ms after the arrival, as 280 + 2 x 150 < 700; the next,
        // at 430, would not end twice over before the guard time, and waits.
        StepsJob job = Queue(new StepsJob(_time, steps: 5, units: 150, stepMs: 150));
        Assert.That(_scheduler.EstimateMs(IlStepKind.Light, 150), Is.EqualTo(150));
        PumpUntil(guard + 100);
        Assert.That(job.Ran, Is.EqualTo(new[] { arrival + 280 }));
        Assert.That(_scheduler.Starved, Is.EqualTo(1), "counted once in the window, however often it looked");
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Guard));
        Assert.That(_scheduler.Pump(0), Is.False, "nothing starts past the guard time");

        // The 10th percentile gap, not the median: one gap of 800 ms among those of a second brings the guard time 200 ms in.
        Make();
        SecondCadence();
        _time.Advance(800 - 30);
        arrival = _time.Ms;
        Process(Historic + _number);
        Assert.That(_scheduler.SlotForTests(), Is.EqualTo((arrival + 500, arrival + 2_000)));
    }

    [Test]
    public void Cadence_comes_from_single_block_branches()
    {
        // A two-block branch catches up: no gap to it or from it counts, so six arrivals around one bring three gaps,
        // too few for a cadence off the tip.
        Make();
        foreach (int count in new[] { 1, 1, 1, 2, 1 })
        {
            Process(Historic + _number, count: count);
            _time.Advance(1_000 - 30);
        }

        Process(Historic + _number);
        Assert.That(_scheduler.SlotForTests(), Is.EqualTo((long.MaxValue, long.MaxValue)));
        PumpUntil(_time.Ms + 300);
        Assert.That((_scheduler.State, _scheduler.Windows), Is.EqualTo((IlSchedulerState.Closed, 0L)));

        // One more single block brings the fourth.
        _time.Advance(1_000 - 300 - 30);
        long arrival = _time.Ms;
        Process(Historic + _number);
        Assert.That(_scheduler.SlotForTests(), Is.EqualTo((arrival + 700, arrival + 2_000)));
    }

    [Test]
    public void Back_to_back()
    {
        // Blocks every 30 ms, as a benchmark sends them with no delay: the quiet time never ends, and a leftover of the
        // shipped list, which the startup compile queued, waits.
        Make();
        Block[] first = Branch(Historic);
        Begin(first);
        StepsJob leftover = Queue(new StepsJob(_time, steps: 1, jobClass: IlJob.ClassShipped));
        _time.Advance(20);
        Complete(first);
        for (int i = 0; i < 200; i++)
        {
            _time.Advance(5);
            Assert.That(_scheduler.Pump(0), Is.False);
            _time.Advance(5);
            Assert.That(_scheduler.Pump(0), Is.False);
            Process(Historic + _number, processingMs: 20);
        }

        Assert.That((leftover.Done, _scheduler.Windows, _scheduler.ClosedBackToBack), Is.EqualTo((0, 0L, 0L)));

        // Once blocks stop, the last quiet time ends too near its next block, which never comes: closed, counted once,
        // however many slots it then misses; idle three seconds on, and the leftover compiles.
        PumpUntil(_time.Ms + 2_900);
        Assert.That((leftover.Done, _scheduler.ClosedBackToBack), Is.EqualTo((0, 1L)));
        PumpUntil(_time.Ms + 120);
        Assert.That((leftover.Done, _scheduler.State), Is.EqualTo((1, IlSchedulerState.Idle)));

        // Every 400 ms the quiet time ends, but too near the next block for a window: closed back to back, once a block
        // from the fifth, which brings the fourth gap.
        Make();
        Begin(first = Branch(Historic));
        leftover = Queue(new StepsJob(_time, steps: 1, jobClass: IlJob.ClassShipped));
        _time.Advance(20);
        Complete(first);
        for (int i = 1; i < 10; i++)
        {
            PumpUntil(_time.Ms + 380);
            Process(Historic + _number, processingMs: 20);
        }

        PumpUntil(_time.Ms + 380);
        Assert.That((leftover.Done, _scheduler.Windows, _scheduler.ClosedBackToBack), Is.EqualTo((0, 0L, 6L)));
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Closed));

        // Every 600 ms a window would have 30 ms, under the 50 a window needs; every 700 ms it has 130, and opens.
        foreach ((int cadence, long windows, long closed) in new[] { (600, 0L, 6L), (700, 6L, 0L) })
        {
            Make();
            Begin(first = Branch(Historic));
            _time.Advance(20);
            Complete(first);
            for (int i = 1; i < 10; i++)
            {
                PumpUntil(_time.Ms + cadence - 20);
                Process(Historic + _number, processingMs: 20);
            }

            PumpUntil(_time.Ms + cadence - 20);
            Assert.That((_scheduler.Windows, _scheduler.ClosedBackToBack), Is.EqualTo((windows, closed)), $"every {cadence} ms");
        }
    }

    [Test]
    public void Idle()
    {
        Make(compileThreads: 1, idleThreads: 3);
        Assert.That(_scheduler.WorkerCount, Is.EqualTo(3));
        StepsJob job = Queue(new StepsJob(_time, steps: 10));

        // No block since startup: idle, and every worker steps, the idle ones too.
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Idle));
        Assert.That((_scheduler.Pump(2), _scheduler.Pump(1), _scheduler.Pump(0)), Is.EqualTo((true, true, true)));

        // A block at the tip: in its window only the window's worker steps.
        Process(TipTimestamp);
        PumpUntil(_time.Ms + 260, worker: 2);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Open, 3)));
        Assert.That(_scheduler.Pump(2), Is.False, "an idle worker sleeps through windows");
        Assert.That(_scheduler.Pump(0), Is.True);

        // No block for two slots at the tip, where no gaps are known yet: idle then, not before, and the idle workers step.
        _time.Advance(23_000);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Open), "the next slot's window, the block's slot missed");
        _time.Advance(1_000);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Idle));
        Assert.That((_scheduler.Pump(2), job.Done), Is.EqualTo((true, 5)));

        // Idle workers by default: a quarter of the cores, at least 1 and at most 4.
        Assert.That(IlSchedulerOptions.From(new IlEvmConfig(), null), Is.EqualTo(new IlSchedulerOptions(250, 300, 1, Math.Clamp(Environment.ProcessorCount / 4, 1, 4), 12)));
        Assert.That(IlSchedulerOptions.From(new IlEvmConfig { CompileThreads = 2, IdleCompileThreads = 3, QuietMs = 200, SlotGuardMs = 400 }, new BlocksConfig { SecondsPerSlot = 5 }),
            Is.EqualTo(new IlSchedulerOptions(200, 400, 2, 3, 5)));
    }

    [Test]
    public void Resumable_job()
    {
        Make();
        // Windows of a second's cadence off the tip run from 280 ms to 700 ms after each arrival: steps of 100 ms start
        // at 280, 380 and 480, three a window, and a 10-step job takes four windows.
        SecondCadence(blocks: 5);
        _time.Advance(1_000 - 30);
        StepsJob job = Queue(new StepsJob(_time, steps: 10, units: 100, stepMs: 100));
        List<(long From, long To)> windows = [];
        for (int i = 0; i < 8 && job.Done < 10; i++)
        {
            long arrival = _time.Ms;
            Process(Historic + _number);
            windows.Add((arrival + 280, arrival + 700));
            PumpUntil(arrival + 1_000);
        }

        Assert.That(job.Done, Is.EqualTo(10));
        Assert.That(windows, Has.Count.EqualTo(4));
        Assert.That(job.Ran.All(t => windows.Any(w => t >= w.From && t + 2 * 100 < w.To)), Is.True, "each step started in a window, with room for twice its estimate");
        Assert.That(job.Ran.GroupBy(t => windows.FindIndex(w => t >= w.From && t < w.To)).Select(static g => g.Count()), Is.EqualTo(new[] { 3, 3, 3, 1 }));
        Assert.That(_scheduler.Steps, Is.EqualTo(10));
    }

    [Test]
    public void Missed_slot_reopen()
    {
        // At the tip: a block two seconds into its slot. The guard time is 300 ms before the next slot starts, and that
        // slot is missed 4 s after its start.
        Make();
        ulong slot = (ulong)(_time.UnixMs / 1000) - 2;
        long arrival = _time.Ms;
        Process(slot, processingMs: 50);
        long next = arrival - 2_000 + 12_000;
        Assert.That(_scheduler.SlotForTests(), Is.EqualTo((next - 300, next + 4_000)));
        StepsJob job = Queue(new StepsJob(_time, steps: 1_000, units: 100, stepMs: 100));
        PumpUntil(next);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Guard));
        int first = job.Done;
        Assert.That(first, Is.GreaterThan(50));
        Assert.That(job.Ran.Max() + 200, Is.LessThan(next - 300));

        // The slot goes by without its block: 4 s into it the next slot's window opens.
        PumpUntil(next + 3_990);
        Assert.That(job.Done, Is.EqualTo(first), "nothing in the guard time");
        PumpUntil(next + 4_000);
        Assert.That((_scheduler.State, _scheduler.Windows), Is.EqualTo((IlSchedulerState.Open, 2L)));
        Assert.That(job.Done, Is.EqualTo(first + 1));
        Assert.That(_scheduler.SlotForTests(), Is.EqualTo((next + 12_000 - 300, next + 16_000)));

        // Off the tip a block counts as come at the miss, twice the median gap after the arrival; idle 3 s after the
        // block's processing ended.
        Make();
        arrival = SecondCadence();
        PumpUntil(arrival + 1_990);
        Assert.That((_scheduler.State, _scheduler.Windows), Is.EqualTo((IlSchedulerState.Guard, 1L)));
        PumpUntil(arrival + 2_000);
        Assert.That((_scheduler.State, _scheduler.Windows), Is.EqualTo((IlSchedulerState.Open, 2L)));
        Assert.That(_scheduler.SlotForTests(), Is.EqualTo((arrival + 2_700, arrival + 4_000)));
        PumpUntil(arrival + 3_020);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Guard));
        PumpUntil(arrival + 30 + 3_000);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Idle));
    }

    [Test]
    public void Syncing_closed()
    {
        // Old blocks a second apart while the node syncs, a cadence that would open windows: none opens, nor after a
        // new head, which follows a branch.
        Make();
        IlEvmSettings.Prefer = IlEvmPrefer.Blocks;
        _head.IsSyncing.Returns(true);
        Block[] block = Branch(Historic + _number);
        Begin(block);
        StepsJob job = Queue(new StepsJob(_time, steps: 3));
        Assert.That((IlRuntime.InBranch, IlRuntime.BlocksCounting), Is.EqualTo((true, false)), "nothing counts while syncing");
        _time.Advance(30);
        Complete(block);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Closed));
        for (int i = 0; i < 10; i++)
        {
            PumpUntil(_time.Ms + 1_000 - 30);
            block = Process(Historic + _number);
            _tree.NewHeadBlock += Raise.EventWith(_tree, new BlockEventArgs(block[0]));
        }

        // Then batches of them.
        for (int i = 0; i < 10; i++)
        {
            PumpUntil(_time.Ms + 40);
            Process(Historic + _number, processingMs: 60, count: 8);
        }

        PumpUntil(_time.Ms + 2_000);
        Assert.That((job.Done, _scheduler.Windows, _scheduler.State), Is.EqualTo((0, 0L, IlSchedulerState.Closed)));

        // Synced, with no block since: idle 3 s after the last ended, and the job runs.
        _head.IsSyncing.Returns(false);
        PumpUntil(_time.Ms + 990);
        Assert.That((job.Done, _scheduler.State), Is.EqualTo((0, IlSchedulerState.Closed)));
        PumpUntil(_time.Ms + 100);
        Assert.That((job.Done, _scheduler.State), Is.EqualTo((3, IlSchedulerState.Idle)));
    }

    [Test]
    public void Busy_through_a_long_branch()
    {
        // A long branch, as a sync's, outlasts the idle time: busy while it runs, closed after it while syncing, and idle
        // only 3 s after it ended.
        Make();
        _head.IsSyncing.Returns(true);
        Block[] batch = Branch(Historic, count: 64);
        Begin(batch);
        StepsJob job = Queue(new StepsJob(_time, steps: 1));
        PumpUntil(_time.Ms + 10_000);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Busy, 0)));
        Complete(batch);
        PumpUntil(_time.Ms + 2_990);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Closed, 0)));
        _head.IsSyncing.Returns(false);
        PumpUntil(_time.Ms + 10);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Idle, 1)));
    }

    [Test]
    public void Starts_as_after_a_quiet_time()
    {
        // A branch the scheduler heard before it started: it starts in the window that branch leaves.
        Make(start: false);
        StepsJob job = Queue(new StepsJob(_time, steps: 1));
        Process(TipTimestamp);
        Assert.That((_scheduler.Pump(0), _scheduler.State), Is.EqualTo((false, IlSchedulerState.Starting)), "nothing before the start");
        _time.Advance(100);
        _scheduler.Start();
        Assert.That((_scheduler.State, _scheduler.Windows), Is.EqualTo((IlSchedulerState.Open, 1L)));
        Assert.That((_scheduler.Pump(0), job.Done), Is.EqualTo((true, 1)));
    }

    [Test]
    public void No_step_while_the_node_builds_a_block()
    {
        // The block producer processes outside the main processing context, which the branch events do not see.
        Make();
        StepsJob job = Queue(new StepsJob(_time, steps: 1));
        _head.IsBuildingBlock.Returns(true);
        Assert.That((_scheduler.Pump(0), job.Done), Is.EqualTo((false, 0)));
        _head.IsBuildingBlock.Returns(false);
        Assert.That((_scheduler.Pump(0), job.Done), Is.EqualTo((true, 1)));
    }

    [Test]
    public void Overlap_accounting()
    {
        Make();
        Process(TipTimestamp);
        _time.Advance(250);
        // The step runs 10 ms, a block starts, and the step runs 40 ms more: 40 ms of it overlapped block processing.
        Block[] next = Branch(TipTimestamp);
        StepsJob job = Queue(new StepsJob(_time, steps: 2, stepMs: 0, during: step =>
        {
            if (step != 0)
                return;
            _time.Advance(10);
            Begin(next);
            _time.Advance(40);
        }));
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((_scheduler.OverlapSteps, _scheduler.OverlapMs), Is.EqualTo((1L, 40.0)));
        Assert.That((_scheduler.State, _scheduler.Pump(0), job.Done), Is.EqualTo((IlSchedulerState.Busy, false, 1)), "no step starts in the block");
        _time.Advance(30);
        Complete(next);
        PumpUntil(_time.Ms + 300);
        Assert.That((job.Done, _scheduler.OverlapSteps), Is.EqualTo((2, 1L)), "a step in a window overlaps nothing");
        Assert.That(_scheduler.Describe(), Does.Contain(" 1 ran into a block by 40 ms;"));
    }

    [Test]
    public void Fork_lookahead_hook()
    {
        // The lookahead itself is a later stage's: the hook runs at the maintenance once a minute, and what it queues in
        // the shipped list's class runs before a chunk queued ahead of it.
        Make();
        StepsJob chunk = Queue(new StepsJob(_time, steps: 1, jobClass: IlJob.ClassChunk));
        List<StepsJob> lookaheads = [];
        IlScheduler.ForkLookahead = scheduler =>
        {
            Assert.That(scheduler, Is.SameAs(_scheduler));
            StepsJob job = new(_time, steps: 1, jobClass: IlJob.ClassLookahead);
            lookaheads.Add(job);
            scheduler.Enqueue(job);
        };
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((lookaheads.Count, lookaheads[0].Done, chunk.Done), Is.EqualTo((1, 1, 0)));
        PumpUntil(_time.Ms + 59_000, every: 1_000);
        Assert.That((lookaheads.Count, chunk.Done), Is.EqualTo((1, 1)));
        PumpUntil(_time.Ms + 2_000, every: 1_000);
        Assert.That(lookaheads, Has.Count.EqualTo(2));
        Assert.That(_scheduler.MaintenanceRuns, Is.InRange(58, 64), "once a second while idle");
    }

    private const int SampleOps = 2_048;
    private const long SampleTicks = 20_480;

    /// <summary>A loss for <paramref name="check"/>: pairs at 1.3 over three windows from <paramref name="window"/>, which an early verdict takes.</summary>
    private static void Lose(IlCheck check, int window = 1)
    {
        for (int w = window; w <= window + 4; w += 2)
        {
            for (int i = 0; i < IlCheck.RecentSamples; i++)
                check.AddCompiled((long)Math.Round(SampleTicks * 1.3), SampleOps);
            for (int i = 0; i < 3; i++)
                check.AddInterpreted(SampleTicks, SampleOps, w);
        }
    }

    /// <summary>The switchback checks on, out of the start-up window, and probations that end as soon as they start.</summary>
    private static void Checks()
    {
        IlEvmSettings.SelfCheck = true;
        IlEvmSettings.QueryCheck = true;
        IlEvmSettings.BlockCheck = true;
        IlEvmSettings.SawFrame();
        IlEvmSettings.SelfCheckWarmUpMs = 0;
        IlEvmSettings.SelfCheckRecheckMs = 0;
        IlCheck.BlockRecheckMs = 0;
    }

    [Test]
    public void Maintenance_ticks_the_scorer_and_ends_probations()
    {
        Checks();
        IlCheck check = Ship(Unique(LoopCode(), 0x71))[0].QueryCheck!;
        Make();
        Assert.That(IlCheck.SchedulerRestores, Is.True, "the scheduler ends the probations");
        List<long> ticks = [];
        IlScheduler.ScorerTick = ticks.Add;

        // Idle from the start: maintenance at once, then once a second.
        _scheduler.Pump(0);
        _scheduler.Pump(0);
        Assert.That(ticks, Is.EqualTo(new[] { _time.Ms }));
        _time.Advance(1_000);
        _scheduler.Pump(0);
        Assert.That(ticks, Has.Count.EqualTo(2));

        // A probation due while block processing runs ends at the next window's maintenance, not before.
        Block[] blocks = Branch(TipTimestamp);
        Begin(blocks);
        Lose(check);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation));
        _time.Advance(1_000);
        Assert.That(_scheduler.Pump(0), Is.False);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation), "not while a block is processed");
        _time.Advance(30);
        Complete(blocks);
        PumpUntil(_time.Ms + 240);
        Assert.That(check.State, Is.EqualTo(IlCheck.Probation), "nor in the quiet time");
        PumpUntil(_time.Ms + 20);
        Assert.That(check.State, Is.EqualTo(IlCheck.Trial), "the window's maintenance ended it");
        Assert.That(ticks, Has.Count.EqualTo(3), "and ticked the scorer at the window's opening");
        // In a long window, once a second.
        PumpUntil(_time.Ms + 2_000);
        Assert.That(ticks, Has.Count.EqualTo(5));

        // And at every window's opening, however soon after the last.
        Process(TipTimestamp);
        PumpUntil(_time.Ms + 260);
        Assert.That((_scheduler.Windows, ticks.Count), Is.EqualTo((2L, 6)));
    }

    [Test]
    public void Maintenance_never_runs_beside_a_step()
    {
        Make(compileThreads: 1, idleThreads: 2);
        List<long> ticks = [];
        bool? besideMaintenance = null, besideStep = null;
        IlScheduler.ScorerTick = now =>
        {
            ticks.Add(now);
            besideMaintenance ??= _scheduler.Pump(1);
        };
        StepsJob job = Queue(new StepsJob(_time, steps: 3, during: step =>
        {
            if (step != 1)
                return;
            _time.Advance(1_000);
            besideStep = _scheduler.Pump(0);
        }));

        // Idle from the start: the maintenance first, during which the other worker starts no step, then a step.
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((ticks.Count, besideMaintenance, job.Done), Is.EqualTo((1, (bool?)false, 1)));

        // A second passes during the other worker's step: the maintenance is due, but waits for the step to end.
        Assert.That(_scheduler.Pump(1), Is.True);
        Assert.That((besideStep, ticks.Count, job.Done), Is.EqualTo(((bool?)false, 1, 2)));

        // Then it runs before the next step.
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((ticks.Count, job.Done), Is.EqualTo((2, 3)));
    }

    [Test]
    public void A_check_s_release_runs_between_blocks()
    {
        Checks();
        foreach ((IlOrigin origin, byte tag) in new[] { (IlOrigin.LiveRpc, (byte)0x72), (IlOrigin.LiveBlocks, (byte)0x73) })
        {
            IlEvm.IlContract promoted = Promote(Unique(LoopCode(), tag), origin);
            IlCheck check = (origin == IlOrigin.LiveBlocks ? promoted.BlockCheck : promoted.QueryCheck)!;
            Make();
            IlRuntime.BlockEpoch = 77;
            Block[] blocks = Branch(TipTimestamp);
            Begin(blocks);

            // A second loss gives the live contract back: its runner leaves it at once, the release is queued.
            Lose(check);
            IlCheck.RestoreDue(check.ViewForTests().RecheckAt);
            Lose(check, window: 11);
            Assert.That(check.State, Is.EqualTo(IlCheck.GivenBack), $"{origin}");
            ReleaseJob release = (ReleaseJob)_scheduler.PendingForTests().Single();
            Assert.That((release.Contract, release.Class), Is.EqualTo((promoted, IlJob.ClassRelease)));
            Assert.That(promoted.Profile!.Live, Is.SameAs(promoted), "not on the frame's thread");
            Assert.That(promoted.Runners.Any(static r => r is not null), Is.True);
            Assert.That(_scheduler.Pump(0), Is.False);

            // Between blocks: released, and the profile demoted for ten half-lives of its source's epochs.
            int rpcEpoch = IlRuntime.RpcEpoch;
            int version = IlRuntime.Version;
            _time.Advance(30);
            Complete(blocks);
            PumpUntil(_time.Ms + 260);
            Assert.That(promoted.Profile.Live, Is.Null);
            Assert.That(promoted.Runners.All(static r => r is null), Is.True, "its code released");
            Assert.That(IlRuntime.Version, Is.GreaterThan(version));
            Assert.That(promoted.Profile.State, Is.EqualTo(IlCodeProfile.LiveDemoted));
            if (origin == IlOrigin.LiveBlocks)
                Assert.That(promoted.Profile.CooldownUntil, Is.EqualTo(77 + 5_120));
            else
                Assert.That(promoted.Profile.CooldownUntil, Is.InRange(rpcEpoch + 6_000, IlRuntime.RpcEpoch + 6_000));
        }
    }

    [Test]
    public void Missing_codes_are_retried_every_ten_minutes()
    {
        byte[] code = Unique(LoopCode(), 0x74);
        ValueHash256 hash = ValueKeccak.Compute(code);
        IDb codeDb = TestMemDbProvider.Init().CodeDb;
        IlShippedBacklog.Record(Bodies(Spec), codeDb, [], [new IlEvmPrecompileList.Entry(hash)], 0, 0);
        Make();
        PumpUntil(_time.Ms + 599_000, every: 1_000);
        Assert.That((_scheduler.Steps, _scheduler.Pending), Is.EqualTo((0L, 0)), "not before ten minutes");
        PumpUntil(_time.Ms + 1_000, every: 1_000);
        Assert.That((_scheduler.Steps, IlShippedBacklog.MissingCount), Is.EqualTo((1L, 1)), "probed, still missing");

        // Its code synced: the next retry finds it, and a job of the shipped class compiles it.
        codeDb[hash.Bytes] = code;
        PumpUntil(_time.Ms + 598_000, every: 1_000);
        Assert.That(IlEvm.IsPrecompiled(hash), Is.False);
        PumpUntil(_time.Ms + 2_000, every: 1_000);
        Drain();
        Assert.That(IlEvm.IsPrecompiled(hash), Is.True);
        Assert.That((IlShippedBacklog.MissingCount, IlShippedBacklog.LeftoverCount, IlShippedBacklog.Compiled), Is.EqualTo((0, 0, 1)));
        CodeInfo codeInfo = Stamped(code);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(Interpret(code, LoopInput)));
        Assert.That(StateOf(codeInfo).QueryRun, Is.Not.Null);
    }

    [Test]
    public void Shipped_leftovers_compile_a_step_at_a_time_in_list_order()
    {
        byte[][] codes = [Unique(LoopCode(), 0x75), Unique(ArithmeticCode(), 0x76), Unique(LoopCode(), 0x77)];
        IDb codeDb = TestMemDbProvider.Init().CodeDb;
        List<IlEvmPrecompileList.Entry> entries = [];
        foreach (byte[] code in codes)
        {
            ValueHash256 hash = ValueKeccak.Compute(code);
            codeDb[hash.Bytes] = code;
            entries.Add(new IlEvmPrecompileList.Entry(hash, Use: IlEvmUse.Main));
        }

        Make();
        IlShippedBacklog.Record(Bodies(Spec), codeDb, entries, [], 0, 0);
        List<ShippedLayoutJob> jobs = [.. entries.Select(static e => new ShippedLayoutJob(e))];
        foreach (ShippedLayoutJob job in jobs)
            Queue(job);
        // A chunk's job queued first still waits behind the shipped list's.
        StepsJob chunk = Queue(new StepsJob(_time, steps: 1, jobClass: IlJob.ClassChunk));

        for (int i = 0; i < jobs.Count; i++)
        {
            ShippedLayoutJob job = jobs[i];
            // The code's read, its layout, then each chunk's JIT in a step of its own; published with the last.
            Assert.That(job.Next.Kind, Is.EqualTo(IlStepKind.Light));
            Assert.That(_scheduler.Pump(0), Is.True);
            Assert.That(job.Next.Kind, Is.EqualTo(IlStepKind.Layout));
            Assert.That(IlShippedBacklog.LeftoverCount, Is.EqualTo(codes.Length - i), "taken, not done");
            Assert.That(_scheduler.Pump(0), Is.True);
            IlEvm.IlContract contract = job.Contract!;
            Assert.That(contract, Is.Not.Null);
            for (int compiled = 0; compiled < contract.ChunkCount; compiled++)
            {
                Assert.That((contract.CompiledChunks, IlEvm.IsPrecompiled(entries[i].Hash)), Is.EqualTo((compiled, false)));
                Assert.That(job.Next.Kind, Is.EqualTo(IlStepKind.Chunk));
                Assert.That(_scheduler.Pump(0), Is.True);
            }

            Assert.That(contract.CompiledChunks, Is.EqualTo(contract.ChunkCount));
            Assert.That(entries.Select(static e => IlEvm.IsPrecompiled(e.Hash)), Is.EqualTo(entries.Select((_, j) => j <= i)), "published in list order");
            Assert.That((contract.Profile!.Shipped, contract.RunMask), Is.EqualTo((contract, (byte)(IlRuntime.RunMain | IlRuntime.RunQuery))));
            Assert.That(IlShippedBacklog.LeftoverCount, Is.EqualTo(codes.Length - 1 - i));
        }

        Assert.That((chunk.Done, IlShippedBacklog.Compiled), Is.EqualTo((0, codes.Length)));
        Assert.That(_scheduler.Describe(), Does.Contain($" ({codes.Length} layouts, {jobs.Sum(static j => j.Contract!.ChunkCount)} chunk JITs, {codes.Length} other)"));
        CodeInfo codeInfo = Stamped(codes[1]);
        RunCounter runs = CountRuns(jobs[1].Contract!);
        Assert.That(Run(QueryVm, codeInfo, ArithmeticInput), Is.EqualTo(Interpret(codes[1], ArithmeticInput)));
        Assert.That(runs.Runs, Is.EqualTo(1), "runs compiled");
    }

    /// <summary>A live contract published for <paramref name="origin"/>'s source, as a promotion does.</summary>
    private static void PublishLive(IlEvm.IlContract contract, byte[] code, IlOrigin origin)
    {
        IlProfileKey key = IlProfileKey.ByHash(ValueKeccak.Compute(code), code.Length);
        IlCodeProfile profile = IlRuntime.Profiles.GetOrAdd(key, static (k, length) => new IlCodeProfile(k, length), code.Length);
        contract.Profile = profile;
        contract.Origin = origin;
        contract.RunMask = IlRuntime.RunAll;
        profile.Live = contract;
        IlRuntime.Grew(block: origin == IlOrigin.LiveBlocks, query: true);
    }

    [Test]
    public void A_promotion_is_laid_out_and_its_chunks_compile_in_the_windows()
    {
        IlEvmSettings.ChunkThreshold = 1;
        byte[] code = Unique(LoopCode(), 0x78);
        Make();
        IlEvm.IlContract? laidOut = null;
        LayoutJob layout = Queue(new LayoutJob(() => code, Bodies(Spec), (contract, reason) =>
        {
            Assert.That(contract, Is.Not.Null, reason);
            laidOut = contract;
            PublishLive(contract!, code, IlOrigin.LiveRpc);
        }));
        Assert.That(layout.Class, Is.EqualTo(IlJob.ClassLayout));
        Assert.That((_scheduler.Pump(0), laidOut), Is.EqualTo((true, (IlEvm.IlContract?)null)), "the code read first");
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That(laidOut!.Runners.All(static r => r is null), Is.True, "laid out, its chunks left to the frames");

        // A frame reaches the entry chunk: it hands back, and the chunk's JIT is queued, once.
        Block[] blocks = Branch(TipTimestamp);
        Begin(blocks);
        CodeInfo codeInfo = Stamped(code);
        Outcome expected = Interpret(code, LoopInput);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        ChunkJob job = (ChunkJob)_scheduler.PendingForTests().Single();
        int entry = laidOut.EntryChunk[0];
        Assert.That((job.Contract, job.Chunk, job.Class), Is.EqualTo((laidOut, entry, IlJob.ClassChunk)));
        Assert.That(job.Next, Is.EqualTo((IlStepKind.Chunk, laidOut.ChunkIlBytes[entry] / 1024.0)));
        Assert.That(laidOut.ChunkIlBytes.Sum(), Is.EqualTo(laidOut.IlBytes));
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(_scheduler.Pending, Is.EqualTo(1), "queued once");

        // Compiled between blocks, then run compiled.
        Assert.That(_scheduler.Pump(0), Is.False, "not while a block is processed");
        Assert.That(laidOut.Runners[entry], Is.Null);
        _time.Advance(30);
        Complete(blocks);
        PumpUntil(_time.Ms + 260);
        Assert.That(laidOut.Runners[entry], Is.Not.Null, "compiled in the window");
        RunCounter runs = CountRuns(laidOut);
        Assert.That(Run(QueryVm, codeInfo, LoopInput), Is.EqualTo(expected));
        Assert.That(runs.Runs, Is.GreaterThan(0));

        // The Legacy rules keep their own background compiler, and leave the scheduler alone.
        IlEvmSettings.Mode = IlMode.Legacy;
        byte[] legacyCode = Unique(LoopCode(), 0x79);
        IlEvm.IlContract? legacy = IlEvm.IlCompiler.Compile(new CodeInfo(legacyCode), IlEvm.IlGetFork(Bodies(Spec)), out string? reason);
        Assert.That(legacy, Is.Not.Null, reason);
        IlRuntime.PublishShipped([(new IlEvmPrecompileList.Entry(ValueKeccak.Compute(legacyCode)), legacy!)]);
        Assert.That(Run(QueryVm, Stamped(legacyCode), LoopInput), Is.EqualTo(Interpret(legacyCode, LoopInput)));
        Stopwatch waited = Stopwatch.StartNew();
        while (legacy!.Runners[legacy.EntryChunk[0]] is null && waited.ElapsedMilliseconds < 10_000)
            Thread.Sleep(5);
        Assert.That(legacy.Runners[legacy.EntryChunk[0]], Is.Not.Null, "the Legacy compile queue compiled it");
        Assert.That(_scheduler.Pending, Is.Zero);
    }

    [Test]
    public void Branch_events_set_the_flags_the_runners_read()
    {
        Make();
        IlEvmSettings.Prefer = IlEvmPrefer.Blocks;
        int epoch = IlRuntime.BlockEpoch;

        // A single block at the tip, not syncing: profiled with Prefer=Blocks, one epoch.
        Block[] blocks = Branch(TipTimestamp);
        Begin(blocks);
        Assert.That((IlRuntime.InBranch, IlRuntime.BlocksCounting, IlRuntime.BlockEpoch), Is.EqualTo((true, true, epoch + 1)));
        Complete(blocks);
        Assert.That((IlRuntime.InBranch, IlRuntime.BlocksCounting), Is.EqualTo((false, false)), "only between a branch's start and its completion");

        // Two blocks still count, each an epoch; three do not.
        Begin(blocks = Branch(TipTimestamp, count: 2));
        Assert.That((IlRuntime.BlocksCounting, IlRuntime.BlockEpoch), Is.EqualTo((true, epoch + 3)));
        Complete(blocks);
        Begin(blocks = Branch(TipTimestamp, count: 3));
        Assert.That((IlRuntime.InBranch, IlRuntime.BlocksCounting, IlRuntime.BlockEpoch), Is.EqualTo((true, false, epoch + 3)));
        Complete(blocks);

        // Nor while syncing, nor with another source.
        _head.IsSyncing.Returns(true);
        Begin(blocks = Branch(TipTimestamp));
        Assert.That(IlRuntime.BlocksCounting, Is.False);
        Complete(blocks);
        _head.IsSyncing.Returns(false);
        IlEvmSettings.Prefer = IlEvmPrefer.Rpc;
        Begin(blocks = Branch(TipTimestamp));
        Assert.That((IlRuntime.InBranch, IlRuntime.BlocksCounting, IlRuntime.BlockEpoch), Is.EqualTo((true, false, epoch + 3)));
        Complete(blocks);

        // Detached, the scheduler hears no more.
        _scheduler.Detach();
        Begin(Branch(TipTimestamp));
        Assert.That(IlRuntime.InBranch, Is.False);
    }

    [Test]
    public void Estimates_learn_from_the_steps()
    {
        Make();
        Assert.That((_scheduler.EstimateMs(IlStepKind.Layout, 24), _scheduler.EstimateMs(IlStepKind.Chunk, 10), _scheduler.EstimateMs(IlStepKind.Light, 1)),
            Is.EqualTo((24.0, 16.0, 1.0)), "1 ms per KiB of code, 1.6 ms per KiB of IL");
        Assert.That(_scheduler.EstimateMs(IlStepKind.Chunk, 0.01), Is.EqualTo(1.6 * 0.25), "a tiny chunk counts as a quarter KiB");

        // Each step moves its kind's rate an eighth of the way toward what it took.
        Queue(new StepsJob(_time, steps: 2, units: 1, stepMs: 3));
        Drain();
        Assert.That(_scheduler.EstimateMs(IlStepKind.Light, 1), Is.EqualTo(1.25 + (3 - 1.25) / 8).Within(1e-9));
        Queue(new StepsJob(_time, steps: 1, units: 2, stepMs: 8, kind: IlStepKind.Chunk));
        Drain();
        Assert.That(_scheduler.EstimateMs(IlStepKind.Chunk, 1), Is.EqualTo(1.6 + (4 - 1.6) / 8).Within(1e-9));
        Assert.That(_scheduler.EstimateMs(IlStepKind.Layout, 1), Is.EqualTo(1.0), "each kind learns its own");
        Assert.That(_scheduler.StepMs, Is.EqualTo(14));
    }

    [Test]
    public void Jobs_wait_by_class_density_and_arrival_up_to_the_cap()
    {
        Make();
        // Busy, so the jobs wait as queued.
        Block[] blocks = Branch(TipTimestamp);
        Begin(blocks);
        StepsJob layout = Queue(new StepsJob(_time, 1, jobClass: IlJob.ClassLayout, density: 9));
        StepsJob light = Queue(new StepsJob(_time, 1, jobClass: IlJob.ClassChunk));
        StepsJob dense = Queue(new StepsJob(_time, 2, jobClass: IlJob.ClassChunk, density: 5));
        StepsJob shipped = Queue(new StepsJob(_time, 1, jobClass: IlJob.ClassShipped));
        StepsJob later = Queue(new StepsJob(_time, 1, jobClass: IlJob.ClassChunk, density: 5));
        StepsJob retry = Queue(new StepsJob(_time, 1, jobClass: IlJob.ClassRetry));
        StepsJob release = Queue(new StepsJob(_time, 1, jobClass: IlJob.ClassRelease));
        Assert.That(_scheduler.PendingForTests(), Is.EqualTo(new IlJob[] { shipped, dense, later, light, layout, release, retry }));

        // A job resumed keeps its place: the dense job's second step runs before the one that came after it.
        _time.Advance(30);
        Complete(blocks);
        PumpUntil(_time.Ms + 260);
        Drain();
        long At(StepsJob job, int step = 0) => job.Ran[step];
        Assert.That(new[] { At(shipped), At(dense), At(dense, 1), At(later), At(light), At(layout), At(release), At(retry) }, Is.Ordered.Ascending);
        Assert.That(_scheduler.Steps, Is.EqualTo(8));

        // Past 4,096 waiting, a job is turned away.
        Begin(blocks = Branch(TipTimestamp));
        for (int i = 0; i < IlScheduler.MaxPending; i++)
            Assert.That(_scheduler.Enqueue(new StepsJob(_time, 1)), Is.True);
        Assert.That(_scheduler.Enqueue(new StepsJob(_time, 1)), Is.False);
        Assert.That(IlScheduler.TryEnqueue(new StepsJob(_time, 1)), Is.False);
        Assert.That((_scheduler.Pending, _scheduler.Rejected), Is.EqualTo((IlScheduler.MaxPending, 2L)));
        Assert.That(_scheduler.Describe(), Does.Contain("; 0 starved, 4096 pending, 2 turned away, 0 failed"));
        Assert.That(_scheduler.Describe(), Does.StartWith("scheduler busy: 1 windows, 0 closed back to back; 8 steps in 0.0 s (0 layouts, 0 chunk JITs, 8 other), 0 ran into a block by 0 ms;"));

        // With no scheduler, nothing is queued; the exit line says so.
        IlScheduler.ResetForTests();
        Assert.That(IlScheduler.TryEnqueue(new StepsJob(_time, 1)), Is.False);
        Assert.That(IlScheduler.DescribeCurrent(), Is.EqualTo("scheduler not attached"));
    }

    /// <summary>A job whose step throws, or that cannot even tell its next step; it counts how often it was dropped.</summary>
    private sealed class FaultyJob(bool inNext) : IlJob(ClassChunk)
    {
        public int Drops;

        public override (IlStepKind Kind, double Units) Next => inNext ? throw new InvalidOperationException("no next step") : (IlStepKind.Light, 1);

        public override bool Step() => throw new InvalidOperationException("broken");

        public override void Dropped() => Drops++;
    }

    [Test]
    public void A_failing_step_drops_its_job_and_the_workers_go_on()
    {
        Make();
        FaultyJob step = Queue(new FaultyJob(inNext: false));
        FaultyJob next = Queue(new FaultyJob(inNext: true));
        StepsJob after = Queue(new StepsJob(_time, 1));
        Assert.That(Drain(), Is.EqualTo(3));
        Assert.That((step.Drops, next.Drops, after.Done, _scheduler.FailedSteps, _scheduler.Pending), Is.EqualTo((1, 1, 1, 2L, 0)));

        // A promotion's layout hears that it failed, so it may try again later.
        List<string?> reasons = [];
        Queue(new LayoutJob(static () => throw new InvalidOperationException("no code"), Bodies(Spec), (contract, reason) => reasons.Add(reason)));
        Drain();
        Assert.That(reasons, Is.EqualTo(new[] { "the layout's step failed" }));

        // A shipped entry whose step threw joins the missing ones, for the next retry, once.
        byte[] code = Unique(LoopCode(), 0x7a);
        ValueHash256 hash = ValueKeccak.Compute(code);
        IDb codeDb = TestMemDbProvider.Init().CodeDb;
        codeDb[hash.Bytes] = code;
        IlEvmPrecompileList.Entry entry = new(hash);
        IlShippedBacklog.Record(Bodies(Spec), codeDb, [entry], [], 0, 0);
        ShippedLayoutJob shipped = new(entry);
        Assert.That(shipped.Step(), Is.False, "its code read");
        Assert.That((IlShippedBacklog.LeftoverCount, IlShippedBacklog.MissingCount), Is.EqualTo((1, 0)));
        shipped.Dropped();
        shipped.Dropped();
        Assert.That((IlShippedBacklog.LeftoverCount, IlShippedBacklog.MissingCount), Is.EqualTo((0, 1)));
    }

    /// <summary>A job of <paramref name="steps"/> steps that notes the thread each ran on.</summary>
    private sealed class ThreadJob(int steps) : IlJob(ClassChunk)
    {
        public readonly ConcurrentQueue<string?> Threads = new();
        private int _done;

        public int Done => Volatile.Read(ref _done);

        public override (IlStepKind Kind, double Units) Next => (IlStepKind.Light, 1);

        public override bool Step()
        {
            Threads.Enqueue(Thread.CurrentThread.Name);
            return Interlocked.Increment(ref _done) == steps;
        }
    }

    [Test]
    public void Idle_soon_after_a_block_starts_only_steps_the_guard_time_holds()
    {
        // Idle 3 s after the last of a few old blocks, with no cadence known: a block may come at any moment, so a step
        // whose twice its estimate passes the 300 ms guard waits, and the jobs behind it; a minute on, idle is idle.
        Make();
        Process(Historic + _number);
        StepsJob longer = Queue(new StepsJob(_time, steps: 1, units: 151));
        StepsJob shorter = Queue(new StepsJob(_time, steps: 1, units: 1));
        PumpUntil(_time.Ms + IlScheduler.IdleAfterMinMs);
        Assert.That((_scheduler.State, longer.Done, shorter.Done), Is.EqualTo((IlSchedulerState.Idle, 0, 0)));
        Assert.That(_scheduler.Starved, Is.EqualTo(1));
        PumpUntil(_time.Ms + IlScheduler.ShallowIdleMs - IlScheduler.IdleAfterMinMs - 100);
        Assert.That((longer.Done, shorter.Done), Is.EqualTo((0, 0)));
        PumpUntil(_time.Ms + 200);
        Assert.That((longer.Done, shorter.Done), Is.EqualTo((1, 1)), "deep in idleness, any step starts");

        // One within the guard time starts at once in a shallow idle; a node that never saw a block has no cap at all.
        Process(Historic + _number);
        StepsJob fits = Queue(new StepsJob(_time, steps: 1, units: 150));
        PumpUntil(_time.Ms + IlScheduler.IdleAfterMinMs + 10);
        Assert.That(fits.Done, Is.EqualTo(1));
        Make();
        StepsJob first = Queue(new StepsJob(_time, steps: 1, units: 5_000));
        Assert.That((_scheduler.Pump(0), first.Done), Is.EqualTo((true, 1)));
    }

    [Test]
    public void A_block_s_arrival_makes_the_scheduler_busy_before_its_branch()
    {
        // The processing queue takes a block in: its senders are recovered and its branch built before the branch's
        // events, and no step starts from the arrival on; the quiet time follows the branch's completion as ever.
        Make();
        StepsJob job = Queue(new StepsJob(_time, steps: 3));
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Idle));
        Block[] blocks = Branch(TipTimestamp);
        Arrive(blocks[0]);
        Assert.That((_scheduler.State, _scheduler.Pump(0)), Is.EqualTo((IlSchedulerState.Busy, false)));
        Assert.That(IlScheduler.MayPublish, Is.False, "nothing published while a block is on its way");
        _time.Advance(40);
        Assert.That(_scheduler.Pump(0), Is.False);
        Begin(blocks);
        _time.Advance(30);
        Complete(blocks);
        Remove(blocks[0]);
        Assert.That((_scheduler.State, IlScheduler.MayPublish), Is.EqualTo((IlSchedulerState.Quiet, true)));
        PumpUntil(_time.Ms + 250);
        Assert.That(job.Done, Is.EqualTo(1), "the window after the quiet time");

        // A block dropped before its processing holds nothing up once the queue lets go of it; nor, after a while, one
        // whose branch never comes.
        Block[] dropped = Branch(TipTimestamp);
        Arrive(dropped[0]);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Busy));
        Remove(dropped[0]);
        Assert.That(_scheduler.State, Is.Not.EqualTo(IlSchedulerState.Busy));
        Arrive(Branch(TipTimestamp)[0]);
        _time.Advance(IlScheduler.ArrivalMs - 1);
        Assert.That(_scheduler.State, Is.EqualTo(IlSchedulerState.Busy));
        _time.Advance(1);
        Assert.That(_scheduler.State, Is.Not.EqualTo(IlSchedulerState.Busy));

        // A step that runs into an arrival overlapped block processing from it.
        Make();
        Block[] next = Branch(TipTimestamp);
        StepsJob into = Queue(new StepsJob(_time, steps: 1, stepMs: 0, during: _ =>
        {
            _time.Advance(10);
            Arrive(next[0]);
            _time.Advance(25);
        }));
        Assert.That(_scheduler.Pump(0), Is.True);
        Assert.That((_scheduler.OverlapSteps, _scheduler.OverlapMs, into.Done), Is.EqualTo((1L, 25.0, 1)));
    }

    [Test]
    public void A_compile_that_ran_into_a_block_publishes_in_a_step_of_its_own()
    {
        // A shipped leftover whose last chunk compiled while a block arrived: published at the next step, in a window,
        // with nothing resolving again beside the block.
        byte[] code = Unique(LoopCode(), 0x7d);
        IDb codeDb = TestMemDbProvider.Init().CodeDb;
        ValueHash256 hash = ValueKeccak.Compute(code);
        codeDb[hash.Bytes] = code;
        IlEvmPrecompileList.Entry entry = new(hash);
        Make();
        IlShippedBacklog.Record(Bodies(Spec), codeDb, [entry], [], 0, 0);
        ShippedLayoutJob job = new(entry);
        Assert.That((job.Step(), job.Step()), Is.EqualTo((false, false)), "read, then laid out");
        Block[] blocks = Branch(TipTimestamp);
        while (job.Contract!.CompiledChunks < job.Contract.ChunkCount - 1)
            Assert.That(job.Step(), Is.False);
        Begin(blocks);
        int version = IlRuntime.Version;
        Assert.That(job.Step(), Is.False, "its last chunk compiled, the publish waits");
        Assert.That((job.Contract.CompiledChunks, IlEvm.IsPrecompiled(hash), IlRuntime.Version), Is.EqualTo((job.Contract.ChunkCount, false, version)));
        Assert.That(job.Next.Kind, Is.EqualTo(IlStepKind.Light));
        Complete(blocks);
        Assert.That(job.Step(), Is.True);
        Assert.That((IlEvm.IsPrecompiled(hash), IlRuntime.Version), Is.EqualTo((true, version + 1)));

        // A promotion's layout, and a release, likewise.
        byte[] liveCode = Unique(LoopCode(), 0x7e);
        IlEvm.IlContract? published = null;
        LayoutJob layout = new(() => liveCode, Bodies(Spec), (contract, _) => published = contract);
        Assert.That(layout.Step(), Is.False, "read");
        Begin(blocks = Branch(TipTimestamp));
        Assert.That((layout.Step(), published), Is.EqualTo((false, (IlEvm.IlContract?)null)), "laid out, not published");
        Assert.That(layout.Next.Kind, Is.EqualTo(IlStepKind.Light));
        ReleaseJob release = new(Compile(Unique(LoopCode(), 0x7f), Spec));
        Assert.That(release.Step(), Is.False);
        Complete(blocks);
        Assert.That((layout.Step(), published), Is.EqualTo((true, published)));
        Assert.That(published, Is.Not.Null);
        Assert.That(release.Step(), Is.True);
        Assert.That(release.Contract.IsReleased, Is.True);
    }

    [Test]
    public void A_node_that_still_syncs_stays_closed_past_the_idle_time()
    {
        // A branch completed while syncing, and the node still syncs 3 s on: closed, not idle, until a minute without a
        // block; once the chain head says it is synced, idle as after any branch.
        Make();
        _head.IsSyncing.Returns(true);
        StepsJob job = Queue(new StepsJob(_time, steps: 2));
        Process(Historic + _number, count: 8);
        PumpUntil(_time.Ms + 10_000);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Closed, 0)));
        PumpUntil(_time.Ms + IlScheduler.SyncingIdleAfterMs - 10_000 - 100);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Closed, 0)));
        PumpUntil(_time.Ms + 200);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Idle, 2)), "a minute with no block");

        Make();
        _head.IsSyncing.Returns(true);
        job = Queue(new StepsJob(_time, steps: 1));
        Process(Historic + _number, count: 8);
        PumpUntil(_time.Ms + 20_000);
        Assert.That(job.Done, Is.Zero);
        _head.IsSyncing.Returns(false);
        PumpUntil(_time.Ms + 200);
        Assert.That((_scheduler.State, job.Done), Is.EqualTo((IlSchedulerState.Idle, 1)), "synced: idle at once, the idle time long gone");
    }

    [Test]
    public void Workers_run_on_threads_of_their_own()
    {
        // The system's clock and real threads: with no block processing at all, idle from the start.
        IlScheduler scheduler = IlScheduler.Attach(new IlSchedulerOptions(250, 300, 1, 2, 12), null, null, null, null, null, threads: true);
        try
        {
            scheduler.Start();
            ThreadJob job = new(steps: 6);
            Assert.That(scheduler.Enqueue(job), Is.True);
            Stopwatch waited = Stopwatch.StartNew();
            while (job.Done < 6 && waited.ElapsedMilliseconds < 10_000)
                Thread.Sleep(5);
            Assert.That(job.Done, Is.EqualTo(6));
            Assert.That(job.Threads.All(static name => name is not null && name.StartsWith("EVM IL scheduler ", StringComparison.Ordinal)), Is.True, string.Join(", ", job.Threads));
            Assert.That(scheduler.State, Is.EqualTo(IlSchedulerState.Idle));
            Assert.That(scheduler.MaintenanceRuns, Is.GreaterThanOrEqualTo(1));
        }
        finally
        {
            scheduler.Stop();
        }

        // Stopped, it takes no more.
        Assert.That(scheduler.Enqueue(new ThreadJob(1)), Is.False);
    }
}
