// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Logging;
using Nethermind.TxPool;

namespace Nethermind.IlEvm;

/// <summary>What the inter-block scheduler lets its workers start.</summary>
internal enum IlSchedulerState
{
    /// <summary>The startup compile runs, before block processing and the calls start; no step starts.</summary>
    Starting,

    /// <summary>Block processing processes a branch: no step starts.</summary>
    Busy,

    /// <summary>The <see cref="IIlEvmConfig.QuietMs"/> after a branch or a new head, left to the commit, forkchoice and calls: no step starts.</summary>
    Quiet,

    /// <summary>A window between blocks: a step starts when twice its estimate ends before the guard time.</summary>
    Open,

    /// <summary>The window's guard time went by: no step starts until the next block, or until its slot counts as missed.</summary>
    Guard,

    /// <summary>No block for a while, or none since startup: steps start with no guard, on the idle workers too.</summary>
    Idle,

    /// <summary>No window: blocks come back to back, the node syncs, or off the chain's tip no cadence is known yet.</summary>
    Closed,
}

/// <summary>How a step's duration is estimated: by KiB of code for a layout, by KiB of IL for a chunk's JIT, by step for the rest.</summary>
internal enum IlStepKind
{
    Layout,
    Chunk,
    Light,
}

/// <summary>The scheduler's knobs, from <see cref="IIlEvmConfig"/> and block processing's slot length.</summary>
/// <param name="SecondsPerSlot">The chain's slot length, which the guard time at the tip is taken from.</param>
internal sealed record IlSchedulerOptions(int QuietMs, int SlotGuardMs, int CompileThreads, int IdleCompileThreads, ulong SecondsPerSlot)
{
    public static IlSchedulerOptions From(IIlEvmConfig config, IBlocksConfig? blocksConfig) =>
        new(Math.Max(0, config.QuietMs), Math.Max(0, config.SlotGuardMs), Math.Clamp(config.CompileThreads, 0, 64),
            config.IdleCompileThreads > 0 ? Math.Min(config.IdleCompileThreads, 64) : Math.Clamp(Environment.ProcessorCount / 4, 1, 4),
            blocksConfig?.SecondsPerSlot ?? 12);
}

/// <summary>
/// The inter-block compile scheduler: every JIT and layout of the Adaptive rules runs on its workers, as steps, in the
/// windows between blocks.
/// </summary>
/// <remarks>
/// The main processing context's branch events drive it (<see cref="IlSchedulerState"/>), as they drive the core's
/// background task scheduler. At the chain's tip the next block's slot gives the guard time; elsewhere, as in a benchmark
/// replaying old blocks, the gaps between recent single-block branches do. The frames' paths never wait on it: a frame
/// only queues a job, under a lock it holds for a moment, once per chunk.
/// </remarks>
internal sealed class IlScheduler
{
    /// <summary>The most jobs waiting; one past it is turned away, and whoever asked keeps the interpreter or asks again later.</summary>
    public const int MaxPending = 4_096;

    /// <summary>A window with less than this to its guard time when it would open stays closed: blocks come back to back.</summary>
    public const int MinWindowMs = 50;

    /// <summary>How long a worker sleeps between looks when nothing wakes it.</summary>
    public const int WakeMs = 10;

    /// <summary>How long a worker that only works while idle sleeps between looks while not idle.</summary>
    public const int IdleWorkerWakeMs = 100;

    /// <summary>The latest gaps between single-block branches kept for the cadence off the chain's tip.</summary>
    public const int Arrivals = 32;

    /// <summary>The gaps a cadence needs; with fewer, off the chain's tip, no window opens.</summary>
    public const int MinGaps = 4;

    /// <summary>The least time with no block after which the scheduler is idle; twice the median gap where that is longer.</summary>
    public const int IdleAfterMinMs = 3_000;

    /// <summary>The idle time after a branch processed while syncing: more batches are likely to come, with gaps between them.</summary>
    public const int SyncingIdleAfterMs = 60_000;

    /// <summary>While idle within this of the latest block, a block may come at any moment: a step starts only when twice its estimate fits in the guard time.</summary>
    public const int ShallowIdleMs = 60_000;

    /// <summary>The longest a block the processing queue took in keeps the scheduler busy before its branch starts, while its senders are recovered and its branch built.</summary>
    public const int ArrivalMs = 5_000;

    /// <summary>At the chain's tip, a slot whose block has not come this long after its start is missed.</summary>
    public const int MissedSlotMs = 4_000;

    /// <summary>How often the maintenance runs in a long window and while idle, besides at each window's opening.</summary>
    public const int MaintenanceEveryMs = 1_000;

    public const int LookaheadEveryMs = 60_000, RetryMissingEveryMs = 600_000, PersistEveryMs = 900_000;

    /// <summary>The estimates' starting rates: a layout per KiB of code, a chunk's JIT per KiB of its IL, any other step.</summary>
    public const double LayoutMsPerKiB = 1.0, ChunkMsPerKiB = 1.6, LightStepMs = 1.0;

    private const double Learning = 1.0 / 8;

    /// <summary>The least size a step's rate is taken over, so a tiny chunk's fixed cost does not swell the rate per KiB.</summary>
    private const double MinUnits = 0.25;

    private const double MinRate = 0.01, MaxRate = 100;

    /// <summary>The busy starts kept, so a step that ran into a block finds when that block started.</summary>
    private const int BusyRing = 8;

    private const int MaxSlotsSkipped = 64;

    private static IlScheduler? _current;

    /// <summary>The scheduler the startup step attached, which jobs are queued to; null before it, and under rules other than the Adaptive ones.</summary>
    public static IlScheduler? Current => Volatile.Read(ref _current);

    /// <summary>Live profiling's scorer, run by each maintenance with the clock in milliseconds: it must return within about a millisecond, going on at the next call.</summary>
    public static Action<long>? ScorerTick { get; set; }

    /// <summary>The fork lookahead: once a minute at a window's maintenance, it queues what compiles the next fork's body set ahead; none yet.</summary>
    public static Action<IlScheduler>? ForkLookahead { get; set; }

    /// <summary>Persistence of what live profiling learned: every 15 minutes at a window's maintenance; none yet.</summary>
    public static Action<IlScheduler>? Persist { get; set; }

    /// <summary>For tests: a scheduler <see cref="Attach"/> makes starts no threads, and the test pumps its workers.</summary>
    internal static bool ManualForTests { get; set; }

    private readonly object _lock = new();
    private readonly IlSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly double _msPerTick;
    private readonly bool _threads;
    private readonly ILogger _logger;
    private readonly PriorityQueue<IlJob, IlJob> _queue = new(JobOrder.Instance);

    private IBranchProcessor? _branchProcessor;
    private IBlockTree? _blockTree;
    private IChainHeadInfoProvider? _chainHeadInfo;
    private IBlockProcessingQueue? _processingQueue;

    /// <summary>Blocks the processing queue took in whose branch has not started, and when the latest came; while some came recently, the scheduler is busy.</summary>
    private readonly HashSet<Hash256> _arriving = [];
    private long _arrivedAt;

    /// <summary>Whether the latest branch completed while the node synced, until the chain head says it no longer does.</summary>
    private bool _syncing;

    private long _syncCheckedAt;

    private const int SyncCheckEveryMs = 100;

    // The state and what moves it, under the lock.
    private IlSchedulerState _state = IlSchedulerState.Starting;
    private ClosedReason _closed;
    private bool _started;
    private volatile bool _stopping;
    private int _branchDepth;
    private long _quietUntil;
    private long _guard = long.MaxValue, _missedAt = long.MaxValue;
    private bool _atTip;
    private long _slotStartUnix;
    private bool _sawBranch, _lastWasSingle;

    /// <summary>When the latest branch started, which the cadence counts from, and when block processing last started or ended one, which idleness counts from.</summary>
    private long _lastBranchAt, _lastBlockAt;
    private readonly long[] _gaps = new long[Arrivals];
    private int _gapCount, _gapNext;
    private long _p10Gap, _medianGap;
    private ulong _headTimestamp;
    private bool _hasHead;
    private bool _maintenanceDue, _maintaining, _retryQueued;
    private long _lastMaintenance = long.MinValue / 2, _lastLookahead = long.MinValue / 2, _lastRetry, _lastPersist;
    private long _busyCount;
    private readonly long[] _busyStarts = new long[BusyRing];
    private long _sequence;
    private int _running;
    private IlJob? _starvedJob;
    private long _starvedWindow = -1;
    private readonly double[] _rates = [LayoutMsPerKiB, ChunkMsPerKiB, LightStepMs];

    private long _windows, _backToBack, _steps, _overlapSteps, _starved, _rejected, _failed, _maintenanceRuns;
    private double _stepMs, _overlapMs, _jitMs;
    private readonly long[] _stepsByKind = new long[3];

    /// <summary>Why the scheduler is closed: whether a missed slot reopens it, or only a block or idleness does.</summary>
    private enum ClosedReason : byte
    {
        None,
        Syncing,
        BackToBack,
        NoCadence,
    }

    internal IlScheduler(IlSchedulerOptions options, TimeProvider time, bool threads, ILogger logger = default)
    {
        _options = options;
        _time = time;
        _msPerTick = 1000.0 / time.TimestampFrequency;
        _threads = threads;
        _logger = logger;
    }

    public int WorkerCount => Math.Max(_options.CompileThreads, _options.IdleCompileThreads);

    public IlSchedulerOptions Options => _options;

    /// <summary>The code database, which live compiles read the code of stamped contracts from; null where the startup had none.</summary>
    public IDb? CodeDb { get; private set; }

    /// <summary>The specs and the block tree, for the fork lookahead's next fork and body set.</summary>
    public ISpecProvider? SpecProvider { get; private set; }

    public IBlockTree? BlockTree => _blockTree;

    /// <summary>Makes the scheduler, subscribes it to block processing's events, and makes it <see cref="Current"/>, which the checks' releases and restores go to.</summary>
    /// <param name="branchProcessor">The main processing context's branch processor; null on a node that processes no blocks, which is then idle.</param>
    /// <param name="processingQueue">The main processing context's queue, whose blocks make the scheduler busy as they arrive, before their branch starts.</param>
    public static IlScheduler Attach(IlSchedulerOptions options, IBranchProcessor? branchProcessor, IBlockTree? blockTree, IChainHeadInfoProvider? chainHeadInfo,
        ISpecProvider? specProvider, IDb? codeDb, ILogger logger = default, TimeProvider? time = null, bool? threads = null, IBlockProcessingQueue? processingQueue = null)
    {
        IlScheduler scheduler = new(options, time ?? TimeProvider.System, threads ?? !ManualForTests, logger)
        {
            CodeDb = codeDb,
            SpecProvider = specProvider,
        };
        scheduler.Subscribe(branchProcessor, blockTree, chainHeadInfo, processingQueue);
        Interlocked.Exchange(ref _current, scheduler)?.Detach();
        // A live contract a check gives back is released in a window; the deciding frame only queues it, or releases at once on a full queue.
        IlCheck.ReleaseLive = static contract =>
        {
            if (!TryEnqueue(new ReleaseJob(contract)))
                Release(contract);
        };
        // From now: the checks' clock leaves due probations to the maintenance while it runs.
        Volatile.Write(ref IlCheck.LastSchedulerRestore, Environment.TickCount64);
        IlCheck.SchedulerRestores = true;
        return scheduler;
    }

    private void Subscribe(IBranchProcessor? branchProcessor, IBlockTree? blockTree, IChainHeadInfoProvider? chainHeadInfo, IBlockProcessingQueue? processingQueue)
    {
        _branchProcessor = branchProcessor;
        _blockTree = blockTree;
        _chainHeadInfo = chainHeadInfo;
        _processingQueue = processingQueue;
        if (branchProcessor is not null)
        {
            branchProcessor.BlocksProcessing += OnBlocksProcessing;
            branchProcessor.BlockProcessing += OnBlockProcessing;
            branchProcessor.BranchProcessingCompleted += OnBranchProcessingCompleted;
        }

        if (blockTree is not null)
            blockTree.NewHeadBlock += OnNewHeadBlock;
        if (processingQueue is not null)
        {
            processingQueue.BlockAdded += OnBlockAdded;
            processingQueue.BlockRemoved += OnBlockRemoved;
        }
    }

    /// <summary>Stops the workers and lets go of the events; jobs still waiting are dropped with the scheduler.</summary>
    internal void Detach()
    {
        Stop();
        if (_branchProcessor is { } branchProcessor)
        {
            branchProcessor.BlocksProcessing -= OnBlocksProcessing;
            branchProcessor.BlockProcessing -= OnBlockProcessing;
            branchProcessor.BranchProcessingCompleted -= OnBranchProcessingCompleted;
        }

        if (_blockTree is { } blockTree)
            blockTree.NewHeadBlock -= OnNewHeadBlock;
        if (_processingQueue is { } processingQueue)
        {
            processingQueue.BlockAdded -= OnBlockAdded;
            processingQueue.BlockRemoved -= OnBlockRemoved;
        }
    }

    /// <summary>Ends <see cref="IlSchedulerState.Starting"/> and starts the workers' threads: below normal priority, under SCHED_IDLE on Linux with <see cref="IlEvmSettings.CompileIdle"/>.</summary>
    /// <remarks>Started after the startup compile, so no thread the runtime starts for its own work inherits their scheduling.</remarks>
    public void Start()
    {
        lock (_lock)
        {
            if (_started || _stopping)
                return;
            _started = true;
            long now = Now;
            _lastRetry = _lastPersist = now;
            // As at the end of a quiet time: idle with no block seen yet, else what the latest branch leaves.
            _state = IlSchedulerState.Quiet;
            _quietUntil = now;
            Advance(now);
        }

        if (!_threads)
            return;
        for (int worker = 0; worker < WorkerCount; worker++)
            new Thread(Work) { IsBackground = true, Name = $"EVM IL scheduler {worker}", Priority = IlEvmSettings.CompilePriority }.Start(worker);
    }

    /// <summary>Stops the workers after the step each runs, as the node shuts down.</summary>
    public void Stop()
    {
        _stopping = true;
        lock (_lock)
            Monitor.PulseAll(_lock);
    }

    public static bool TryEnqueue(IlJob job) => Current?.Enqueue(job) ?? false;

    /// <summary>Whether a job may publish what it compiled, or release what it held: not while a block is under way, beside which states would resolve again.</summary>
    public static bool MayPublish => Current?.BlockUnderWay() != true;

    private bool BlockUnderWay()
    {
        lock (_lock)
            return _branchDepth > 0 || (_arriving.Count > 0 && Now - _arrivedAt < ArrivalMs);
    }

    /// <summary>Queues <paramref name="job"/>, behind those of its class and density that came before it; false when <see cref="MaxPending"/> wait.</summary>
    public bool Enqueue(IlJob job)
    {
        lock (_lock)
        {
            if (_stopping || _queue.Count >= MaxPending)
            {
                _rejected++;
                return false;
            }

            job.Sequence = ++_sequence;
            _queue.Enqueue(job, job);
            Monitor.PulseAll(_lock);
            return true;
        }
    }

    /// <summary>Releases a live contract a check gave back: its profile is demoted for a cooldown, its code goes, and every state resolves again.</summary>
    internal static void Release(IlEvm.IlContract contract)
    {
        if (contract.Profile is { } profile && ReferenceEquals(profile.Live, contract))
            profile.Demote(contract.Origin);
        IlCheck.ReleaseNow(contract);
    }

    private void Work(object? index)
    {
        int worker = (int)index!;
        if (IlEvmSettings.CompileIdle && OperatingSystem.IsLinux())
            IlEvm.IlCompileQueue.SetIdleScheduling();
        while (!_stopping)
        {
            try
            {
                while (!_stopping && Pump(worker))
                {
                }
            }
            catch (Exception exception)
            {
                // A step's own failure is counted where it ran; nothing else may take a worker down.
                if (_logger.IsWarn)
                    _logger.Warn($"IL-EVM scheduler: worker {worker} failed: {exception}");
            }

            lock (_lock)
            {
                if (!_stopping)
                    Monitor.Wait(_lock, worker < _options.CompileThreads || _state == IlSchedulerState.Idle ? WakeMs : IdleWorkerWakeMs);
            }
        }
    }

    /// <summary>One look of a worker: the state brought up to now, the maintenance when due, then the first job's next step if it may start; true when a step ran.</summary>
    internal bool Pump(int worker)
    {
        RefreshSyncing();
        bool maintain = false;
        lock (_lock)
        {
            long now = Now;
            Advance(now);
            if (!MayWork(worker))
                return false;
            // The maintenance never runs beside a step, so the scorer sees jobs' work whole: once due, no step starts until it has run.
            if (_maintaining)
                return false;
            if (_maintenanceDue || now - _lastMaintenance >= MaintenanceEveryMs)
            {
                if (_running > 0)
                    return false;
                _maintenanceDue = false;
                _maintaining = true;
                _lastMaintenance = now;
                maintain = true;
            }
        }

        if (maintain)
            Maintain();

        // A block this node builds is processed outside the main processing context, unseen by the branch events.
        if (BuildingBlock())
            return false;

        IlJob? job;
        IlStepKind kind = IlStepKind.Light;
        double units = 1;
        long busy, started;
        Exception? broken = null;
        lock (_lock)
        {
            long now = Now;
            Advance(now);
            if (!MayWork(worker) || _maintaining || !_queue.TryPeek(out job, out _))
                return false;
            try
            {
                (kind, units) = job.Next;
            }
            catch (Exception exception)
            {
                // A job that cannot say what its step is would stop every job behind it.
                _queue.Dequeue();
                broken = exception;
            }

            if (broken is null && !Fits(now, EstimateLocked(kind, units)))
            {
                // The first job waits for a window with room for its step, and so does every job behind it.
                Starve(job);
                return false;
            }

            if (broken is null)
            {
                _queue.Dequeue();
                _running++;
            }

            busy = _busyCount;
            started = _time.GetTimestamp();
        }

        if (broken is not null)
        {
            Drop(job, broken);
            return true;
        }

        bool done;
        TimeSpan jitBefore = System.Runtime.JitInfo.GetCompilationTime(currentThread: true);
        try
        {
            done = job.Step();
        }
        catch (Exception exception)
        {
            done = true;
            Drop(job, exception);
        }

        double jitMs = (System.Runtime.JitInfo.GetCompilationTime(currentThread: true) - jitBefore).TotalMilliseconds;
        lock (_lock)
        {
            long ended = _time.GetTimestamp();
            double ms = (ended - started) * _msPerTick;
            _running--;
            _steps++;
            _stepMs += ms;
            _jitMs += jitMs;
            _stepsByKind[(int)kind]++;
            Learn(kind, units, ms);
            if (_busyCount != busy)
            {
                // A block started while the step ran: the step's time after that start overlapped block processing.
                double busyAt = _busyStarts[(busy + 1) % BusyRing];
                _overlapSteps++;
                _overlapMs += Math.Max(0, ended * _msPerTick - Math.Max(started * _msPerTick, busyAt));
            }

            // Its sequence keeps its place: a job resumed runs before those that came after it.
            if (!done)
                _queue.Enqueue(job, job);
        }

        return true;
    }

    private void Drop(IlJob job, Exception exception)
    {
        Interlocked.Increment(ref _failed);
        if (_logger.IsWarn)
            _logger.Warn($"IL-EVM scheduler: a step of {job.Name} failed, and the job is dropped: {exception}");
        try
        {
            job.Dropped();
        }
        catch (Exception dropException)
        {
            // What it held stays held; the workers go on.
            IlEvmSettings.RecordCaught($"dropping {job.Name}", dropException);
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: {job.Name} failed to let go of what it holds: {dropException}");
        }
    }

    private bool BuildingBlock()
    {
        try
        {
            return _chainHeadInfo?.IsBuildingBlock == true;
        }
        catch (Exception exception)
        {
            IlEvmSettings.RecordCaught("building-block check", exception);
            return false;
        }
    }

    /// <summary>Whether a step estimated at <paramref name="estimateMs"/> may start now in the state, under the lock.</summary>
    private bool Fits(long now, double estimateMs) => _state switch
    {
        IlSchedulerState.Open => now + 2 * estimateMs < _guard,
        IlSchedulerState.Idle => !_sawBranch || now - _lastBlockAt >= ShallowIdleMs || 2 * estimateMs <= Math.Max(MinWindowMs, _options.SlotGuardMs),
        _ => true,
    };

    private bool MayWork(int worker) => _state switch
    {
        IlSchedulerState.Open => worker < _options.CompileThreads,
        IlSchedulerState.Idle => worker < WorkerCount,
        _ => false,
    };

    /// <summary>The scorer, the checks' restores, and what runs every minute or more, on the worker that claimed the maintenance; outside the lock, as each may queue jobs.</summary>
    private void Maintain()
    {
        try
        {
            long now = Now;
            ScorerTick?.Invoke(now);
            // Due probations and masks end here, between blocks, on the checks' own clock; their timer ends them only when this has not run for a while.
            long checksNow = Environment.TickCount64;
            IlCheck.RestoreDue(checksNow);
            Volatile.Write(ref IlCheck.LastSchedulerRestore, checksNow);
            bool lookahead, retry, persist;
            lock (_lock)
            {
                lookahead = now - _lastLookahead >= LookaheadEveryMs;
                if (lookahead)
                    _lastLookahead = now;
                retry = !_retryQueued && now - _lastRetry >= RetryMissingEveryMs;
                if (retry)
                    _lastRetry = now;
                persist = now - _lastPersist >= PersistEveryMs;
                if (persist)
                    _lastPersist = now;
            }

            if (lookahead)
                ForkLookahead?.Invoke(this);
            // Read outside the lock: the backlog takes its own.
            if (retry && IlShippedBacklog.MissingCount > 0 && Enqueue(new RetryMissingJob(this)))
            {
                lock (_lock)
                    _retryQueued = true;
            }

            if (persist)
                Persist?.Invoke(this);
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: maintenance failed: {exception}");
        }
        finally
        {
            lock (_lock)
            {
                _maintaining = false;
                _maintenanceRuns++;
            }
        }
    }

    /// <summary>The retry of missing codes probed every entry: another may be queued ten minutes on.</summary>
    internal void RetryDone()
    {
        lock (_lock)
            _retryQueued = false;
    }

    /// <summary>Moves the state on to <paramref name="now"/>, under the lock: the quiet time's end, the guard time, a missed slot, idleness.</summary>
    private void Advance(long now)
    {
        if (!_started)
        {
            _state = IlSchedulerState.Starting;
            return;
        }

        if (_branchDepth > 0)
        {
            _state = IlSchedulerState.Busy;
            return;
        }

        if (_arriving.Count > 0)
        {
            if (now - _arrivedAt < ArrivalMs)
            {
                _state = IlSchedulerState.Busy;
                return;
            }

            // None came to a branch in time, as blocks dropped before their processing: what they held up goes on.
            _arriving.Clear();
        }

        if (_state == IlSchedulerState.Busy)
            EndBusy(now);
        if (_state == IlSchedulerState.Quiet && now < _quietUntil)
            return;
        if (_state != IlSchedulerState.Idle && IdleDue(now))
        {
            _state = IlSchedulerState.Idle;
            _closed = ClosedReason.None;
            _maintenanceDue = true;
            return;
        }

        if (_state == IlSchedulerState.Quiet)
            OpenWindow(now, afterQuiet: true);
        else if (_state == IlSchedulerState.Open && now >= _guard)
            _state = IlSchedulerState.Guard;

        // The awaited block missed its slot: a guard or a back-to-back closing moves on to the next slot's window.
        for (int skipped = 0; (_state == IlSchedulerState.Guard || (_state == IlSchedulerState.Closed && _closed == ClosedReason.BackToBack)) && now >= _missedAt; skipped++)
        {
            if (skipped == MaxSlotsSkipped)
            {
                _state = IlSchedulerState.Idle;
                _closed = ClosedReason.None;
                _maintenanceDue = true;
                return;
            }

            NextSlot(now);
            OpenWindow(now, afterQuiet: false);
            if (_state == IlSchedulerState.Open && now >= _guard)
                _state = IlSchedulerState.Guard;
        }
    }

    private bool IdleDue(long now) => !_sawBranch || now - _lastBlockAt >= IdleAfterMs();

    /// <summary>Past a synced node's idle time after a syncing branch, asks the chain head, outside the lock, whether the node still syncs.</summary>
    private void RefreshSyncing()
    {
        if (!Volatile.Read(ref _syncing))
            return;
        long now = Now;
        if (now - Volatile.Read(ref _lastBlockAt) < IdleAfterMinMs || now - Volatile.Read(ref _syncCheckedAt) < SyncCheckEveryMs)
            return;
        Volatile.Write(ref _syncCheckedAt, now);
        // The chain head's word may take the block tree's locks, which a branch's events hold when they take this one.
        if (IsSyncing())
            return;
        lock (_lock)
            _syncing = false;
    }

    /// <summary>How long with no block makes the scheduler idle; at the tip a slot stands in for the median gap until the gaps are known.</summary>
    private long IdleAfterMs()
    {
        long median = _gapCount >= MinGaps ? _medianGap : _atTip ? SlotMs : 0;
        return Math.Max(_syncing ? SyncingIdleAfterMs : IdleAfterMinMs, 2 * median);
    }

    private long SlotMs => (long)Math.Min(_options.SecondsPerSlot, 86_400UL) * 1000;

    /// <summary>The quiet time ended, or a slot was missed: a window to the guard time, unless it is too short to open or no guard time is known.</summary>
    /// <param name="afterQuiet">Whether a quiet time ended, rather than a slot was missed: a closing is counted once a branch.</param>
    private void OpenWindow(long now, bool afterQuiet)
    {
        if (_guard == long.MaxValue)
        {
            _state = IlSchedulerState.Closed;
            _closed = ClosedReason.NoCadence;
        }
        else if (_guard - now < MinWindowMs)
        {
            _state = IlSchedulerState.Closed;
            _closed = ClosedReason.BackToBack;
            if (afterQuiet)
                _backToBack++;
        }
        else
        {
            _state = IlSchedulerState.Open;
            _closed = ClosedReason.None;
            _windows++;
            _maintenanceDue = true;
        }
    }

    /// <summary>When the next block is expected, from the head; the chain's tip is the head within two slots of the wall clock.</summary>
    private void ComputeGuard(long now)
    {
        long unixNow = UnixNow;
        long slotMs = SlotMs;
        long headMs = _hasHead ? (long)Math.Min(_headTimestamp, (ulong)(long.MaxValue / 4_000)) * 1000 : 0;
        _atTip = _hasHead && slotMs > 0 && headMs + 2 * slotMs >= unixNow;
        if (_atTip)
        {
            _slotStartUnix = headMs + slotMs;
            SetSlot(now, unixNow);
        }
        else if (_gapCount >= MinGaps)
        {
            _guard = _lastBranchAt + _p10Gap - _options.SlotGuardMs;
            _missedAt = _lastBranchAt + Math.Max(1, 2 * _medianGap);
        }
        else
        {
            _guard = _missedAt = long.MaxValue;
        }
    }

    /// <summary>The slot after the one missed: at the tip the chain's next slot; elsewhere a block counted as if it had come at the miss.</summary>
    private void NextSlot(long now)
    {
        if (_atTip)
        {
            _slotStartUnix += SlotMs;
            SetSlot(now, UnixNow);
            return;
        }

        long from = _missedAt;
        _guard = from + _p10Gap - _options.SlotGuardMs;
        _missedAt = from + Math.Max(1, 2 * _medianGap);
    }

    private void SetSlot(long now, long unixNow)
    {
        long offset = unixNow - now;
        _guard = _slotStartUnix - _options.SlotGuardMs - offset;
        _missedAt = _slotStartUnix + MissedSlotMs - offset;
    }

    private void AddGap(long gap)
    {
        _gaps[_gapNext] = Math.Max(0, gap);
        _gapNext = (_gapNext + 1) % Arrivals;
        _gapCount = Math.Min(_gapCount + 1, Arrivals);
        Span<long> sorted = stackalloc long[_gapCount];
        _gaps.AsSpan(0, _gapCount).CopyTo(sorted);
        sorted.Sort();
        _p10Gap = sorted[(_gapCount - 1) / 10];
        _medianGap = (_gapCount & 1) == 1 ? sorted[_gapCount / 2] : (sorted[_gapCount / 2 - 1] + sorted[_gapCount / 2]) / 2;
    }

    /// <summary>Counts the first job starved once per window, however many looks find it waiting.</summary>
    private void Starve(IlJob job)
    {
        if (_starvedWindow == _windows && ReferenceEquals(_starvedJob, job))
            return;
        _starvedWindow = _windows;
        _starvedJob = job;
        _starved++;
    }

    public double EstimateMs(IlStepKind kind, double units)
    {
        lock (_lock)
            return EstimateLocked(kind, units);
    }

    private double EstimateLocked(IlStepKind kind, double units) => _rates[(int)kind] * Math.Max(units, MinUnits);

    private void Learn(IlStepKind kind, double units, double ms)
    {
        ref double rate = ref _rates[(int)kind];
        rate = Math.Clamp(rate + (ms / Math.Max(units, MinUnits) - rate) * Learning, MinRate, MaxRate);
    }

    private long Now => (long)(_time.GetTimestamp() * _msPerTick);

    private long UnixNow => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private bool IsSyncing()
    {
        try
        {
            if (_chainHeadInfo is { } chainHeadInfo)
                return chainHeadInfo.IsSyncing;
            return _blockTree is { } blockTree && blockTree.IsSyncing(maxDistanceForSynced: 16).isSyncing;
        }
        catch (Exception exception)
        {
            IlEvmSettings.RecordCaught("syncing check", exception);
            return false;
        }
    }

    /// <summary>A branch starts, on block processing's thread: no step starts until it completes.</summary>
    private void OnBlocksProcessing(object? sender, BlocksProcessingEventArgs e)
    {
        try
        {
            int count = e.Blocks.Count;
            bool syncing = IsSyncing();
            lock (_lock)
            {
                long now = Now;
                if (_branchDepth++ == 0)
                {
                    // Busy from the block's arrival, when the queue's event came first.
                    if (_state != IlSchedulerState.Busy)
                        StartBusy(now);
                    if (_started)
                        _state = IlSchedulerState.Busy;
                }

                // The cadence comes from consecutive single-block branches; a longer one is catching up.
                if (count == 1 && _lastWasSingle && _sawBranch)
                    AddGap(now - _lastBranchAt);
                _lastWasSingle = count == 1;
                _lastBranchAt = _lastBlockAt = now;
                _sawBranch = true;
                // The arrivals the branch takes over: the branch holds the scheduler busy from here.
                for (int i = 0; i < count; i++)
                {
                    if (e.Blocks[i].Hash is { } hash)
                        _arriving.Remove(hash);
                }
            }

            IlRuntime.InBranch = true;
            IlRuntime.BlocksCounting = IlEvmSettings.Mode == IlMode.Adaptive && IlEvmSettings.Prefer == IlEvmPrefer.Blocks && count <= 2 && !syncing;
        }
        catch (Exception exception)
        {
            // On block processing's path: nothing of the scheduler may fail a block.
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: a branch's start was not handled: {exception}");
        }
    }

    private void OnBlockAdded(object? sender, BlockEventArgs e)
    {
        try
        {
            if (e.Block.Hash is not { } hash)
                return;
            lock (_lock)
            {
                long now = Now;
                if (_branchDepth == 0 && _state != IlSchedulerState.Busy)
                    StartBusy(now);
                _arriving.Add(hash);
                _arrivedAt = now;
                _lastBlockAt = now;
                if (_started)
                    _state = IlSchedulerState.Busy;
            }
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: a block's arrival was not handled: {exception}");
        }
    }

    /// <summary>The processing queue let go of a block: one that never reached a branch, as one dropped before its processing, holds the scheduler busy no more.</summary>
    private void OnBlockRemoved(object? sender, BlockRemovedEventArgs e)
    {
        try
        {
            lock (_lock)
            {
                if (_arriving.Remove(e.BlockHash) && _arriving.Count == 0 && _branchDepth == 0 && _state == IlSchedulerState.Busy)
                    EndBusy(Now);
            }
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: a block's removal was not handled: {exception}");
        }
    }

    /// <summary>A busy time starts, under the lock: a step running now ran into a block, from here.</summary>
    private void StartBusy(long now)
    {
        _busyCount++;
        _busyStarts[_busyCount % BusyRing] = now;
    }

    /// <summary>A busy time with no branch to complete it ends, under the lock: the scheduler goes on as after a quiet time.</summary>
    private void EndBusy(long now)
    {
        if (!_started)
            return;
        _state = IlSchedulerState.Quiet;
        _quietUntil = Math.Max(_quietUntil, now);
    }

    private void OnBlockProcessing(object? sender, BlockEventArgs e)
    {
        if (IlRuntime.BlocksCounting)
            Interlocked.Increment(ref IlRuntime.BlockEpoch);
    }

    /// <summary>The branch completed, its blocks committed: the quiet time starts, or the scheduler closes while the node syncs.</summary>
    private void OnBranchProcessingCompleted(object? sender, BranchProcessingCompletedEventArgs e)
    {
        try
        {
            bool syncing = IsSyncing();
            Block? last = e.ProcessedBlocksCount > 0 ? e.SuggestedBlocks[e.ProcessedBlocksCount - 1] : null;
            lock (_lock)
            {
                if (_branchDepth > 0 && --_branchDepth > 0)
                    return;
                long now = Now;
                _lastBlockAt = now;
                if (last is not null)
                {
                    _headTimestamp = last.Timestamp;
                    _hasHead = true;
                }

                ComputeGuard(now);
                _syncing = syncing;
                if (_started)
                {
                    if (syncing)
                    {
                        _state = IlSchedulerState.Closed;
                        _closed = ClosedReason.Syncing;
                    }
                    else
                    {
                        _state = IlSchedulerState.Quiet;
                        _quietUntil = now + _options.QuietMs;
                    }
                }
            }

            IlRuntime.BlocksCounting = false;
            IlRuntime.InBranch = false;
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: a branch's completion was not handled: {exception}");
        }
    }

    /// <summary>A new head, after a branch or from a forkchoice update: the quiet time starts again, and the guard time follows the new head.</summary>
    private void OnNewHeadBlock(object? sender, BlockEventArgs e)
    {
        try
        {
            lock (_lock)
            {
                _headTimestamp = e.Block.Timestamp;
                _hasHead = true;
                if (!_started || _branchDepth > 0 || (_state == IlSchedulerState.Closed && _closed == ClosedReason.Syncing))
                    return;
                long now = Now;
                ComputeGuard(now);
                _state = IlSchedulerState.Quiet;
                _quietUntil = Math.Max(_quietUntil, now + _options.QuietMs);
            }
        }
        catch (Exception exception)
        {
            if (_logger.IsWarn)
                _logger.Warn($"IL-EVM scheduler: a new head was not handled: {exception}");
        }
    }

    public IlSchedulerState State
    {
        get
        {
            lock (_lock)
            {
                Advance(Now);
                return _state;
            }
        }
    }

    public int Pending
    {
        get
        {
            lock (_lock)
                return _queue.Count;
        }
    }

    /// <summary>Windows opened, and windows that did not open because the guard time was too close.</summary>
    public long Windows => Read(ref _windows);

    public long ClosedBackToBack => Read(ref _backToBack);

    public long Steps => Read(ref _steps);

    public double StepMs
    {
        get
        {
            lock (_lock)
                return _stepMs;
        }
    }

    /// <summary>The milliseconds of JIT the steps took, on the workers' threads.</summary>
    public double JitMs
    {
        get
        {
            lock (_lock)
                return _jitMs;
        }
    }

    /// <summary>Steps a block started during, and the milliseconds of them after it started.</summary>
    public long OverlapSteps => Read(ref _overlapSteps);

    public double OverlapMs
    {
        get
        {
            lock (_lock)
                return _overlapMs;
        }
    }

    /// <summary>Windows in which the first job waited for want of time before the guard.</summary>
    public long Starved => Read(ref _starved);

    /// <summary>Jobs turned away by a full queue, and steps that threw.</summary>
    public long Rejected => Read(ref _rejected);

    public long FailedSteps => Interlocked.Read(ref _failed);

    public long MaintenanceRuns => Read(ref _maintenanceRuns);

    private long Read(ref long counter)
    {
        lock (_lock)
            return counter;
    }

    /// <summary>The guard time and the missed time of the slot the scheduler waits for, on its clock; <see cref="long.MaxValue"/> for none. For tests.</summary>
    internal (long Guard, long MissedAt) SlotForTests()
    {
        lock (_lock)
            return (_guard, _missedAt);
    }

    /// <summary>The jobs waiting, in the order they will run; for tests.</summary>
    internal List<IlJob> PendingForTests()
    {
        lock (_lock)
        {
            List<IlJob> jobs = [];
            foreach ((IlJob job, IlJob _) in _queue.UnorderedItems)
                jobs.Add(job);
            jobs.Sort(JobOrder.Instance);
            return jobs;
        }
    }

    /// <summary>The scheduler for the exit line.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            Advance(Now);
            return string.Create(CultureInfo.InvariantCulture,
                $"scheduler {_state.ToString().ToLowerInvariant()}: {_windows} windows, {_backToBack} closed back to back; " +
                $"{_steps} steps in {_stepMs / 1000:F1} s ({_stepsByKind[(int)IlStepKind.Layout]} layouts, {_stepsByKind[(int)IlStepKind.Chunk]} chunk JITs, {_stepsByKind[(int)IlStepKind.Light]} other), " +
                $"{_overlapSteps} ran into a block by {_overlapMs:F0} ms; {_jitMs / 1000:F1} s of JIT in the steps; " +
                $"{_starved} starved, {_queue.Count} pending, {_rejected} turned away, {Interlocked.Read(ref _failed)} failed");
        }
    }

    public static string DescribeCurrent() => Current?.Describe() ?? "scheduler not attached";

    /// <summary>Lets go of the <see cref="Current"/> scheduler and the hooks, for tests; the checks' hooks are the checks' to reset.</summary>
    internal static void ResetForTests()
    {
        Interlocked.Exchange(ref _current, null)?.Detach();
        ScorerTick = null;
        ForkLookahead = null;
        Persist = null;
    }

    /// <summary>Jobs in the order they run: by class, then the denser first, then in the order they came.</summary>
    private sealed class JobOrder : IComparer<IlJob>
    {
        public static readonly JobOrder Instance = new();

        public int Compare(IlJob? x, IlJob? y)
        {
            int byClass = x!.Class.CompareTo(y!.Class);
            if (byClass != 0)
                return byClass;
            int byDensity = y.Density.CompareTo(x.Density);
            return byDensity != 0 ? byDensity : x.Sequence.CompareTo(y.Sequence);
        }
    }
}

/// <summary>Work for the scheduler's windows: one or more steps, each short enough to end within a guard time.</summary>
/// <param name="jobClass">The job's class: lower classes run first.</param>
/// <param name="density">Within a class, the denser job runs first: what it gains per millisecond, as its maker scores it.</param>
internal abstract class IlJob(int jobClass, double density = 0)
{
    /// <summary>The shipped list's entries left by the startup compile, and the fork lookahead's compiles: before everything else.</summary>
    public const int ClassShipped = 0;

    public const int ClassLookahead = 0;

    /// <summary>A chunk that frames reached before it was compiled, which they wait for on the interpreter.</summary>
    public const int ClassChunk = 1;

    /// <summary>A promotion's layout, whose chunks then compile as frames reach them.</summary>
    public const int ClassLayout = 2;

    public const int ClassRelease = 3;

    public const int ClassRetry = 4;

    public readonly int Class = jobClass;

    public readonly double Density = density;

    internal long Sequence;

    /// <summary>The next step's kind and size, in its kind's units (<see cref="IlStepKind"/>), for its estimate.</summary>
    public abstract (IlStepKind Kind, double Units) Next { get; }

    /// <summary>Runs the next step, on a worker, outside the scheduler's lock.</summary>
    /// <returns>Whether the job is done; one that is not goes back to its place in the queue.</returns>
    public abstract bool Step();

    /// <summary>A step threw, and the scheduler dropped the job: it lets go of what it holds, as a list entry it took.</summary>
    public virtual void Dropped()
    {
    }

    public virtual string Name => GetType().Name;
}

/// <summary>The JIT of one chunk of a laid-out contract that frames reached <see cref="IlEvmSettings.ChunkThreshold"/> times: frames hand back to the interpreter at it until it is done.</summary>
internal sealed class ChunkJob(IlEvm.IlContract contract, int chunk) : IlJob(ClassChunk)
{
    public IlEvm.IlContract Contract => contract;

    public int Chunk => chunk;

    public override (IlStepKind Kind, double Units) Next => (IlStepKind.Chunk, contract.ChunkIlBytes[chunk] / 1024.0);

    /// <summary>Compiles the chunk, unless the contract was released meanwhile or the chunk failed.</summary>
    public override bool Step()
    {
        contract.CompileChunk(chunk);
        return true;
    }
}

/// <summary>A shipped list entry the startup compile left: read, laid out for the startup's body set, each chunk compiled in a step of its own, then published.</summary>
internal sealed class ShippedLayoutJob(IlEvmPrecompileList.Entry entry) : IlJob(ClassShipped)
{
    private const int Reading = 0, LayingOut = 1, Compiling = 2, Publishing = 3;

    private int _stage;
    private byte[]? _code;
    private OpcodeBodySet? _bodies;
    private IlEvm.IlContract? _contract;
    private int _chunk;

    /// <summary>Whether the job took its entry off the backlog and has not handed it back.</summary>
    private bool _taken;

    public IlEvmPrecompileList.Entry Entry => entry;

    internal IlEvm.IlContract? Contract => _contract;

    public override (IlStepKind Kind, double Units) Next => _stage switch
    {
        Reading or Publishing => (IlStepKind.Light, 1),
        LayingOut => (IlStepKind.Layout, _code!.Length / 1024.0),
        _ => (IlStepKind.Chunk, _contract!.ChunkIlBytes[_chunk] / 1024.0),
    };

    public override bool Step()
    {
        switch (_stage)
        {
            case Reading:
                // Gone from the backlog when something else compiled it meanwhile.
                if (!IlShippedBacklog.Take(entry, out OpcodeBodySet? bodies, out IDb? codeDb))
                    return true;
                _taken = true;
                // The code the entry carries, as the RPC list's do, else the code database's.
                byte[]? code = IlShippedBacklog.CodeOf(codeDb, entry, out bool mismatched);
                if (mismatched)
                {
                    IlEvmSettings.CountFailed(IlShippedBacklog.MismatchedCode);
                    return Finish(IlShippedBacklog.Outcome.Declined, null);
                }

                if (code is null || code.Length == 0)
                    return Finish(IlShippedBacklog.Outcome.Missing, null);

                _code = code;
                _bodies = bodies;
                _stage = LayingOut;
                return false;
            case LayingOut:
                IlEvm.IlContract? contract = null;
                string? reason;
                try
                {
                    contract = IlEvm.IlCompiler.Compile(new CodeInfo(_code!), IlEvm.IlGetFork(_bodies!), out reason);
                }
                catch (Exception exception)
                {
                    reason = exception.Message;
                }

                _code = null;
                if (contract is null)
                {
                    IlEvmSettings.CountFailed(reason);
                    return Finish(IlShippedBacklog.Outcome.Declined, null);
                }

                // Never released, as the startup compile's: a verdict against it keeps only its checker's runner off it.
                contract.Check.Name = contract.Name;
                _contract = contract;
                _stage = Compiling;
                _chunk = -1;
                return NextChunk();
            case Compiling:
                _contract!.CompileChunk(_chunk);
                return NextChunk();
            default:
                return Publish();
        }
    }

    private bool NextChunk()
    {
        IlEvm.IlContract contract = _contract!;
        while (++_chunk < contract.ChunkCount && Volatile.Read(ref contract.Runners[_chunk]) is not null)
        {
        }

        if (_chunk < contract.ChunkCount)
            return false;
        _stage = Publishing;
        return Publish();
    }

    private bool Publish() => IlScheduler.MayPublish && Finish(IlShippedBacklog.Outcome.Compiled, _contract);

    private bool Finish(IlShippedBacklog.Outcome outcome, IlEvm.IlContract? contract)
    {
        _taken = false;
        IlShippedBacklog.Finish(entry, outcome, contract);
        return true;
    }

    /// <summary>A step threw, as a read of the code database may: the entry joins the missing ones, for the next retry.</summary>
    public override void Dropped()
    {
        if (_taken)
            Finish(IlShippedBacklog.Outcome.Missing, null);
    }
}

/// <summary>A promotion's layout: the code read, then laid out for <paramref name="bodies"/>; <paramref name="laidOut"/> publishes it, or hears why it was declined.</summary>
internal sealed class LayoutJob(Func<ReadOnlyMemory<byte>> code, OpcodeBodySet bodies, Action<IlEvm.IlContract?, string?> laidOut, double density = 0)
    : IlJob(ClassLayout, density)
{
    private ReadOnlyMemory<byte> _code;
    private bool _read, _laidOut, _reported;
    private IlEvm.IlContract? _contract;
    private string? _reason;

    public override (IlStepKind Kind, double Units) Next => _read && !_laidOut ? (IlStepKind.Layout, _code.Length / 1024.0) : (IlStepKind.Light, 1);

    public override bool Step()
    {
        if (!_read)
        {
            _code = code();
            _read = true;
            if (!_code.IsEmpty)
                return false;
            return Report(null, "no code");
        }

        if (!_laidOut)
        {
            try
            {
                _contract = IlEvm.IlCompiler.Compile(new CodeInfo(_code), IlEvm.IlGetFork(bodies), out _reason);
            }
            catch (Exception exception)
            {
                _reason = exception.Message;
            }

            _code = default;
            _laidOut = true;
        }

        return IlScheduler.MayPublish && Report(_contract, _reason);
    }

    /// <summary>Tells the promotion how the layout went, once; with <see cref="IlEvmSettings.EagerChunks"/> each chunk of a contract it published is then a job of its own.</summary>
    private bool Report(IlEvm.IlContract? contract, string? reason)
    {
        _reported = true;
        laidOut(contract, reason);
        if (IlEvmSettings.EagerChunks && contract is not null && !contract.IsReleased)
        {
            for (int chunk = 0; chunk < contract.ChunkCount; chunk++)
                IlScheduler.TryEnqueue(new ChunkJob(contract, chunk));
        }

        return true;
    }

    /// <summary>A step threw: the promotion hears that the layout failed, so it may try again later.</summary>
    public override void Dropped()
    {
        if (!_reported)
            Report(null, "the layout's step failed");
    }
}

/// <summary>The release of a live contract a switchback check gave back, between blocks: its runner's bits went at the verdict, its code goes here.</summary>
internal sealed class ReleaseJob(IlEvm.IlContract contract) : IlJob(ClassRelease)
{
    public IlEvm.IlContract Contract => contract;

    public override (IlStepKind Kind, double Units) Next => (IlStepKind.Light, 1);

    public override bool Step()
    {
        if (!IlScheduler.MayPublish)
            return false;
        IlScheduler.Release(contract);
        return true;
    }
}

/// <summary>A retry of the list entries whose code the code database lacked, as at genesis before a sync: each found now gets a <see cref="ShippedLayoutJob"/>.</summary>
internal sealed class RetryMissingJob(IlScheduler scheduler) : IlJob(ClassRetry)
{
    public const int ProbesPerStep = 32;

    private List<IlEvmPrecompileList.Entry>? _missing;
    private int _next;

    public override (IlStepKind Kind, double Units) Next => (IlStepKind.Light, 1);

    public override bool Step()
    {
        _missing ??= IlShippedBacklog.Missing();
        int end = Math.Min(_missing.Count, _next + ProbesPerStep);
        for (; _next < end; _next++)
        {
            IlEvmPrecompileList.Entry entry = _missing[_next];
            if (IlShippedBacklog.Found(entry))
                scheduler.Enqueue(new ShippedLayoutJob(entry));
        }

        if (_next < _missing.Count)
            return false;
        scheduler.RetryDone();
        return true;
    }

    public override void Dropped() => scheduler.RetryDone();
}

/// <summary>Live profiling's word on a code, which only the scheduler's threads write: a release's demotion and its cooldown.</summary>
internal sealed partial class IlCodeProfile
{
    /// <summary>Values of <see cref="State"/>: nothing decided, promoted by live profiling, and demoted, in a cooldown.</summary>
    public const byte LiveNone = 0, LivePromoted = 1, LiveDemoted = 2;

    public byte State;

    /// <summary>Until which epoch of the source that promoted it a demoted code may not be promoted again: <see cref="IlRuntime.BlockEpoch"/> or <see cref="IlRuntime.RpcEpoch"/>.</summary>
    public long CooldownUntil;

    /// <summary>The live contract of <paramref name="origin"/>'s source was released: demoted, in a cooldown of ten of that source's half-lives (<see cref="IlLiveParams.CooldownEpochs"/>).</summary>
    internal void Demote(IlOrigin origin)
    {
        bool block = origin == IlOrigin.LiveBlocks;
        State = LiveDemoted;
        BelowTicks = 0;
        CooldownUntil = IlLive.Epoch(block) + IlLive.For(block).CooldownEpochs;
    }
}
