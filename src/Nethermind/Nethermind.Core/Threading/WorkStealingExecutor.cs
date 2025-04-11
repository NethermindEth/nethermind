// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Core.Threading;

// Its like rust's rayon, but worst.
public class WorkStealingExecutor<T>: IDisposable where T: IJob<T>
{
    private uint _roundRobinStealIdx = 0;

    private readonly int _workerCount = 0;
    private readonly Context<T>[] _workerContexts;
    private readonly DStack<Context<T>> _guestContextPool = new DStack<Context<T>>(0);

    private int _asleepWorker = 0;
    private readonly bool[] _sleepingContexts;
    private readonly ConcurrentQueue<int> _sleepingContextQueue;
    private readonly int _initialStackSize;
    private Context<T>[] _guestContexts = [];

    public WorkStealingExecutor(int workerCount, int initialStackSize)
    {
        _initialStackSize = initialStackSize;

        _workerCount = workerCount;
        _workerContexts = new Context<T>[workerCount];

        _sleepingContexts = new bool[workerCount];
        _sleepingContextQueue = new ConcurrentQueue<int>();

        for (int i = 0; i < workerCount; i++)
        {
            _workerContexts[i] = new Context<T>(this, i, initialStackSize);
        }
        for (int i = 0; i < workerCount; i++)
        {
            _workerContexts[i].StartWorker();
        }
    }

    public void Execute(T job)
    {
        var ctx = CreateGuestContext();
        job.Execute(ctx);
        ReturnGuestContext(ctx);
    }

    private Context<T> CreateGuestContext()
    {
        if (!_guestContextPool.TryPop(out Context<T>? context))
        {
            context = new Context<T>(this, -1, _initialStackSize);
        }

        while (true)
        {
            Context<T>[] currentPool = _guestContexts;
            Context<T>[] newContexts = new Context<T>[currentPool.Length + 1];
            currentPool.CopyTo(newContexts, 0);
            newContexts[currentPool.Length] = context!;
            if (Interlocked.CompareExchange(ref _guestContexts, newContexts, currentPool) == currentPool)
            {
                break;
            }
        }

        return context!;
    }

    private void ReturnGuestContext(Context<T> context)
    {
        while (true)
        {
            Context<T>[] currentPool = _guestContexts;
            Context<T>[] newContexts = currentPool.Where(c => c != context).ToArray();
            if (Interlocked.CompareExchange(ref _guestContexts, newContexts, currentPool) == currentPool)
            {
                break;
            }
        }

        _guestContextPool.Push(context);
    }

    public bool TryStealJob(out JobRef<T> job, out bool shouldRetry, int nextContextToCheck)
    {
        shouldRetry = false;
        int activeContexts = _workerCount;

        if (nextContextToCheck < 0)
        {
            nextContextToCheck = (int)(Interlocked.Increment(ref _roundRobinStealIdx) % _workerCount);
        }

        for (int i = 0; i < activeContexts; i++)
        {
            int idx = (nextContextToCheck % activeContexts);
            nextContextToCheck++;

            Context<T> context = _workerContexts[idx];
            if (context.TryStealFrom(out job!, out bool contextSaidShouldRetry))
            {
                return true;
            }

            if (contextSaidShouldRetry) shouldRetry = true;
        }

        Context<T>[] guestContexts = _guestContexts;
        int guestContextsCount = guestContexts.Length;
        for (int i = 0; i < guestContextsCount; i++)
        {
            int idx = (nextContextToCheck % guestContextsCount);
            nextContextToCheck++;

            Context<T> context = guestContexts[idx];
            if (context.TryStealFrom(out job!, out bool contextSaidShouldRetry))
            {
                return true;
            }

            if (contextSaidShouldRetry) shouldRetry = true;
        }

        job = default;
        return false;
    }

    public void AddToSleepingWorker(int contextIdx)
    {
        if (contextIdx < 0) return;
        Interlocked.Increment(ref _asleepWorker);

        if (Interlocked.CompareExchange(ref _sleepingContexts[contextIdx], true, false) == false)
        {
            _sleepingContextQueue.Enqueue(contextIdx);
        }
    }

    public void RemoveFromSleepingWorker(int contextIdx)
    {
        if (contextIdx < 0) return;
        Interlocked.Decrement(ref _asleepWorker);
    }

    private long _statsNotify = 0;
    private long _statsNotifyQueueEmpty = 0;
    private long _statsNotifyOk = 0;
    private long _statsNotifyComplete = 0;

    public void NotifyNewJob(int fromContext, int count = 1)
    {
        // HOT PATH
        if (count == 0) return;
        if (_asleepWorker == 0) return;

        Interlocked.Increment(ref _statsNotify);
        for (int i = 0; i < _workerCount; i++)
        {
            if (!_sleepingContextQueue.TryDequeue(out int ctxId))
            {
                Interlocked.Increment(ref _statsNotifyQueueEmpty);
                break;
            }
            Interlocked.CompareExchange(ref _sleepingContexts[ctxId], false, true);

            Context<T> context = _workerContexts[ctxId];
            if (context.TryWakeUp(fromContext))
            {
                Interlocked.Increment(ref _statsNotifyOk);
                count--;
                if (count == 0)
                {
                    Interlocked.Increment(ref _statsNotifyComplete);
                    break;
                }
            }
        }
    }

    public void PrintDebug()
    {
        foreach (var workerContext in _workerContexts)
        {
            workerContext.PrintDebugStats();
        }

        Console.Error.WriteLine($"SN {_statsNotify,5}  SNQE {_statsNotifyQueueEmpty,5}  SNOK {_statsNotifyOk,5}  SNCOMP {_statsNotifyComplete,5}");
    }

    public void Dispose()
    {
        // TODO: Incoming job not empty
        for (int i = 0; i < _workerCount; i++)
        {
            _workerContexts[i].Dispose();
        }
    }
}

public class Context<T>: IDisposable where T: IJob<T>
{
    private DStack<JobRef<T>> _jobStack;
    private Stack<ManualResetEventSlim> _latchPool;
    private readonly WorkStealingExecutor<T> _executor;
    private readonly int _contextIdx;
    private int _nextContextToCheck;
    private long _jobIdCounter = 0;

    private ManualResetEventSlim _finishLatch;
    private ManualResetEventSlim _sleepLatch;
    private const int RoundUntilSleep = 32;
    private const int LatchWaitMs = 100;
    private int _waitRound = 0;
    private bool _isAsleep = false;
    private long _wakeUpTime = 0;
    private Thread? _workerThread = null;


    public Context(WorkStealingExecutor<T> executor, int contextIdx, int initialStackSize)
    {
        _executor = executor;
        _contextIdx = contextIdx;
        _jobStack = new(initialStackSize);
        _latchPool = new(initialStackSize);
        _finishLatch = new ManualResetEventSlim(false);
        _sleepLatch = new ManualResetEventSlim(false);
    }

    public void StartWorker()
    {
        _workerThread = new Thread(WorkerLoop);
        _workerThread.Start();
    }

    public int ContextIdx => _contextIdx;

    public void Fork(T job1, T job2)
    {
        var latch = PushJob(job2);
        job1.Execute(this);
        WaitForJobOrKeepBusy(latch);
    }

    public JobRef<T> PushJob(T job, bool notify = true)
    {
        if (!_latchPool.TryPop(out ManualResetEventSlim? resetEvent))
        {
            resetEvent = new ManualResetEventSlim(false, 1);
        }
        else
        {
            resetEvent.Reset();
        }

        JobRef<T> jobRef = new(job, _jobIdCounter++, resetEvent);

        // HOT PATH
        _jobStack.Push(jobRef);
        if (notify) _executor.NotifyNewJob(ContextIdx);

        return jobRef;
    }

    public void MultiPushJob(ReadOnlySpan<T> jobs, Span<JobRef<T>> latches)
    {
        RefList16<JobRef<T>> jobRefs = new RefList16<JobRef<T>>();

        for (int i = 0; i < jobs.Length; i++)
        {
            if (!_latchPool.TryPop(out ManualResetEventSlim? resetEvent))
            {
                resetEvent = new ManualResetEventSlim(false, 1);
            }
            else
            {
                resetEvent.Reset();
            }

            JobRef<T> jobRef = new(jobs[i], _jobIdCounter++, resetEvent);
            jobRefs.Add(jobRef);
            latches[i] = jobRef;
        }

        // HOT PATH
        if (jobs.Length != 0)
        {
            _jobStack.PushMany(jobRefs.AsSpan());
            _executor.NotifyNewJob(ContextIdx, jobs.Length);
        }
    }

    public void WaitForJobOrKeepBusy(JobRef<T> jobRef)
    {
        // Announce looking here
        ResetWaitCounter();

        while (!jobRef.ResetEvent.IsSet)
        {
            if (_jobStack.TryPop(out JobRef<T> otherRef))
            {
                ResetWaitCounter();
                if (otherRef.Id == jobRef.Id)
                {
                    // Inline execute
                    otherRef.ExecuteInline(this);
                    // Recycle latcch
                    _latchPool.Push(jobRef.ResetEvent);
                    return;
                }

                otherRef!.ExecuteNonInline(this);
            }
            else if (_executor.TryStealJob(out otherRef, out bool shouldRetry, -1))
            {
                _statsSteal++;
                ResetWaitCounter();
                otherRef!.ExecuteNonInline(this);
            }
            else
            {
                _statsNoJob++;
                if (shouldRetry) _statsStealRetry++;
                if (!shouldRetry)
                {
                    _sleepLatch.Reset();
                    OnNoJob(jobRef.ResetEvent.WaitHandle);
                }
            }
        }

        // Recycle latcch
        _latchPool.Push(jobRef.ResetEvent);
    }

    public void ResetWaitCounter()
    {
        _waitRound = 0;
    }

    private void OnNoJob(WaitHandle? latch)
    {
        _waitRound++;
        if (_waitRound > RoundUntilSleep)
        {
            if (Interlocked.CompareExchange(ref _isAsleep, true, false) == false)
            {
                _executor.AddToSleepingWorker(ContextIdx);
            }

            _statsWaited++;
            if (latch != null)
            {
                WaitHandle.WaitAny([latch, _sleepLatch.WaitHandle], LatchWaitMs);
            }
            else
            {
                _sleepLatch.Wait(LatchWaitMs);
            }
            if (_wakeUpTime != 0)
            {
                _wakeUpTimes.Add((_wakeUpTime, Stopwatch.GetTimestamp() - _wakeUpTime));
                _wakeUpTime = 0;
            }

            if (Interlocked.CompareExchange(ref _isAsleep, false, true) == true)
            {
                _executor.RemoveFromSleepingWorker(ContextIdx);
            }
            return;
        }

        _statsYielded++;
        Thread.Yield();
    }

    private void WorkerLoop()
    {
        try
        {
            while (!_finishLatch.IsSet)
            {
                bool ok = _executor.TryStealJob(out JobRef<T> jobRef, out bool shouldRetry, _nextContextToCheck);
                if (ok)
                {
                    _statsSteal++;
                    jobRef.ExecuteNonInline(this);
                    ResetWaitCounter();
                    continue;
                }

                _nextContextToCheck = -1;

                if (shouldRetry) _statsStealRetry++;
                if (!shouldRetry)
                {
                    _sleepLatch.Reset();
                    OnNoJob(null);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error in worker loop {ex}");
            throw;
        }
    }

    public bool TryWakeUp()
    {
        if (Interlocked.CompareExchange(ref _isAsleep, false, true) == true)
        {
            _wakeUpTime = Stopwatch.GetTimestamp();
            _sleepLatch.Set();
            _executor.RemoveFromSleepingWorker(ContextIdx);
            return true;
        }

        return false;
    }

    public bool TryStealFrom(out JobRef<T> job, out bool shouldRetry)
    {
        bool ok = _jobStack.TryDequeue(out job!, out shouldRetry);
        if (ok)
        {
            _statsStolenFrom++;
        }
        return ok;
    }

    public bool TryWakeUp(int nextContextToCheck)
    {
        _nextContextToCheck = nextContextToCheck;
        bool ok = TryWakeUp();
        if (ok)
        {
            _statsWokeUpNotify++;
        }

        return ok;
    }

    public void Dispose()
    {
        _finishLatch.Set();
        if (_workerThread is not null)
        {
            _workerThread.Join();
        }
    }

    public void RunJob16(ref RefList16<T> jobs)
    {
        if (jobs.Count == 0) return;
        if (jobs.Count == 1)
        {
            jobs[0]!.Execute(this);
            return;
        }


        RefList16<JobRef<T>> jobLatches = new RefList16<JobRef<T>>(jobs.Count - 1);

        if (jobs.Count > 1)
        {
            MultiPushJob(jobs.AsSpan()[1..], jobLatches.AsSpan());
        }

        // Inline
        jobs[0]!.Execute(this);

        // Wait for the rest
        for (int i = jobLatches.Count - 1; i >= 0; i--)
        {
            // In reverse order
            WaitForJobOrKeepBusy(jobLatches[i]!);
        }
    }

    private long _statsSteal = 0;
    private long _statsStealRetry = 0;
    private long _statsStolenFrom = 0;
    private long _statsNoJob;
    private long _statsWokeUpNotify;
    private long _statsYielded = 0;
    private long _statsWaited = 0;
    internal List<(long, long)> _wakeUpTimes = new List<(long, long)>();

    public void PrintDebugStats()
    {
        long actualSteal = _statsSteal;
        long actualStealRetry = _statsStealRetry;

        Console.Error.WriteLine($"{ContextIdx,5}  ST {actualSteal,5}  STR {actualStealRetry,5}  SF {_statsStolenFrom,5}  NJ {_statsNoJob,5}  W {_statsWokeUpNotify,5}  WY {_statsYielded}  WW {_statsWaited}");
        foreach (var workerWakeUpTime in _wakeUpTimes)
        {
            Console.Error.WriteLine($"T {workerWakeUpTime.Item1} {TimeSpan.FromTicks(workerWakeUpTime.Item2).TotalMilliseconds}");
        }
    }
}

public interface IJob<T> where T : IJob<T>
{
    public void Execute(Context<T> ctx);
}

public struct JobRef<T>(T job, long id, ManualResetEventSlim _resetEvent) where T : IJob<T>
{
    public long Id = id;

    public ManualResetEventSlim ResetEvent { get; } = _resetEvent;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteInline(Context<T> context)
    {
        job.Execute(context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteNonInline(Context<T> context)
    {
        // HOT PATH
        job.Execute(context);

        ResetEvent.Set();
    }
}
