// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Threading;
using Nethermind.Core.Collections;

namespace Nethermind.Core.Threading;

// Its like rust's rayon, but worst.
public class WorkStealingExecutor: IDisposable
{
    private readonly int _workerCount = 0;

    private int _asleepWorker = 0;
    private uint _roundRobinStealIdx = 0;
    // private uint _wakeUpCounter = 0;

    private readonly Context[] _workerContexts;
    private readonly ConcurrentQueue<JobRef> _incomingJob = new ConcurrentQueue<JobRef>();

    private readonly bool[] _sleepingContexts;
    private readonly ConcurrentQueue<int> _sleepingContextQueue;

    public WorkStealingExecutor(int workerCount, int initialStackSize)
    {
        _workerCount = workerCount;
        _workerContexts = new Context[workerCount];

        _sleepingContexts = new bool[workerCount];
        _sleepingContextQueue = new ConcurrentQueue<int>();

        for (int i = 0; i < workerCount; i++)
        {
            _workerContexts[i] = new Context(this, i, initialStackSize);
        }
        for (int i = 0; i < workerCount; i++)
        {
            _workerContexts[i].StartWorker();
        }
    }

    public void Execute(IJob job)
    {
        // TODO: Way to enroll current thread.
        JobRef jobRef = new JobRef(job, 0, new ManualResetEventSlim(false, 1));
        _incomingJob.Enqueue(jobRef);
        NotifyNewJob(0);
        jobRef.ResetEvent.Wait();
    }

    public bool TryStealJob(out JobRef job, out bool shouldRetry, int currentCtx, int nextContextToCheck)
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
            // nextContextToCheck += _workerCount-1; // check the previous contexts.
            nextContextToCheck++; // check the previous contexts.

            Context context = _workerContexts[idx];
            if (context.TryStealFrom(out job!, out bool contextSaidShouldRetry))
            {
                return true;
            }

            if (contextSaidShouldRetry) shouldRetry = true;
        }

        if (_incomingJob.TryDequeue(out job!))
        {
            return true;
        }

        return false;
    }

    public void AddToSleepingWorker(int contextIdx)
    {
        Interlocked.Increment(ref _asleepWorker);

        if (Interlocked.CompareExchange(ref _sleepingContexts[contextIdx], true, false) == false)
        {
            _sleepingContextQueue.Enqueue(contextIdx);
        }
    }

    public void RemoveFromSleepingWorker(int contextIdx)
    {
        Interlocked.Decrement(ref _asleepWorker);

        /*
        if (Interlocked.CompareExchange(ref _sleepingContexts[contextIdx], false, true) == true)
        {
            // Maybe try to remove?
        }
        */
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

        /*
        var searchOrder = _ctxSearchOrder[fromContext];
        for (int i = 0; i < _workerCount; i++)
        {
            // if (!_sleepingContextQueue.TryDequeue(out int ctxId)) break;
            // Interlocked.CompareExchange(ref _sleepingContexts[ctxId], false, true);
            Context context = _workerContexts[searchOrder[i]];
            if (context.TryWakeUp(fromContext))
            {
                count--;
                if (count == 0) break;
            }
        }
        */

        Interlocked.Increment(ref _statsNotify);
        for (int i = 0; i < _workerCount; i++)
        {
            if (!_sleepingContextQueue.TryDequeue(out int ctxId))
            {
                Interlocked.Increment(ref _statsNotifyQueueEmpty);
                break;
            }
            Interlocked.CompareExchange(ref _sleepingContexts[ctxId], false, true);

            Context context = _workerContexts[ctxId];
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

public class Worker: IDisposable
{
    private const int RoundUntilSleep = 32;
    private const int LatchWaitMs = 100;

    private readonly WorkStealingExecutor _executor;
    private readonly Context _context;
    private readonly Thread _thread;

    private ManualResetEventSlim _finishLatch;
    private ManualResetEvent _sleepLatch;
    private bool _isAsleep = false;
    private int _waitRound = 0;

    public Worker(Context context, WorkStealingExecutor executor)
    {
        _context = context;
        _executor = executor;
        _finishLatch = new ManualResetEventSlim(false);
        _sleepLatch = new ManualResetEvent(false);
        _thread = new Thread(WorkerLoop);
    }

    public void Start()
    {
        _thread.UnsafeStart();
    }

    private void WorkerLoop()
    {
        try
        {
            while (!_finishLatch.IsSet)
            {
                if (_context.TryStealJob(out JobRef jobRef, out bool shouldRetry))
                {
                    jobRef.ExecuteNonInline(_context);
                    continue;
                }

                if (shouldRetry) _statsExecuteStolenRetry++;
                if (!shouldRetry)
                {
                    _sleepLatch.Reset();
                    OnNoJob(_sleepLatch);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error in worker loop {ex}");
            throw;
        }
    }

    public void Dispose()
    {
        _finishLatch.Set();
        _thread.Join();
    }

    public bool TryWakeUp()
    {
        if (Interlocked.CompareExchange(ref _isAsleep, false, true) == true)
        {
            // _nextContextToCheck = nextContextToCheck;
            _executor.RemoveFromSleepingWorker(_context.ContextIdx);
            _sleepLatch.Set();
            return true;
        }

        return false;
    }

    public void ResetWaitCounter()
    {
        _waitRound = 0;
    }

    internal long _statsExecuteStolenRetry = 0;
    internal long _statsYielded = 0;
    internal long _statsWaited = 0;

    public void OnNoJob(WaitHandle latch)
    {
        _waitRound++;
        if (_waitRound > RoundUntilSleep)
        {
            if (Interlocked.CompareExchange(ref _isAsleep, true, false) == false)
            {
                _executor.AddToSleepingWorker(_context.ContextIdx);
            }

            _statsWaited++;
            latch.WaitOne(LatchWaitMs);

            if (Interlocked.CompareExchange(ref _isAsleep, false, true) == true)
            {
                _executor.RemoveFromSleepingWorker(_context.ContextIdx);
            }
            return;
        }

        _statsYielded++;
        Thread.Yield();
    }
}

public class Context: IDisposable
{
    private DStack<JobRef> _jobStack;
    private Stack<ManualResetEventSlim> _latchPool;
    private readonly WorkStealingExecutor _executor;
    private readonly int _contextIdx;
    private readonly Worker _worker;
    private int _nextContextToCheck;
    private long _jobId = 0;

    public Context(WorkStealingExecutor executor, int contextIdx, int initialStackSize)
    {
        _executor = executor;
        _contextIdx = contextIdx;
        _jobStack = new(initialStackSize);
        _latchPool = new(initialStackSize);
        _worker = new Worker(this, executor);
    }

    public void StartWorker()
    {
        _worker.Start();
    }

    public int ContextIdx => _contextIdx;

    public void Fork(IJob job1, IJob job2)
    {
        var latch = PushJob(job2);
        job1.Execute(this);
        WaitForJobOrKeepBusy(latch);
    }

    public JobRef PushJob(IJob job, bool notify = true)
    {
        if (!_latchPool.TryPop(out ManualResetEventSlim? resetEvent))
        {
            resetEvent = new ManualResetEventSlim(false, 1);
        }
        else
        {
            resetEvent.Reset();
        }

        JobRef jobRef = new(job, _jobId++, resetEvent);

        // HOT PATH
        _jobStack.Push(jobRef);
        if (notify) _executor.NotifyNewJob(ContextIdx);

        return jobRef;
    }

    public void MultiPushJob(ReadOnlySpan<IJob> jobs, Span<JobRef> latches)
    {
        RefList16<JobRef> jobRefs = new RefList16<JobRef>();

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

            JobRef jobRef = new(jobs[i], _jobId++, resetEvent);
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

    public void WaitForJobOrKeepBusy(JobRef jobRef)
    {
        // Announce looking here
        _worker.ResetWaitCounter();

        while (!jobRef.ResetEvent.IsSet)
        {
            if (_jobStack.TryPop(out JobRef otherRef))
            {
                _worker.ResetWaitCounter();
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
            else if (_executor.TryStealJob(out otherRef, out bool shouldRetry, ContextIdx, -1))
            {
                _statsSteal++;
                _worker.ResetWaitCounter();
                otherRef!.ExecuteNonInline(this);
            }
            else
            {
                _statsNoJob++;
                if (shouldRetry) _statsStealRetry++;
                if (!shouldRetry) _worker.OnNoJob(jobRef.ResetEvent.WaitHandle);
            }
        }

        // Recycle latcch
        _latchPool.Push(jobRef.ResetEvent);
    }

    public bool TryStealJob(out JobRef jobRef, out bool shouldRetry)
    {
        bool ok = _executor.TryStealJob(out jobRef, out shouldRetry, ContextIdx, _nextContextToCheck);
        if (ok)
        {
            _statsSteal++;
            _worker.ResetWaitCounter();
        }

        _nextContextToCheck = -1;
        return ok;
    }

    public bool TryStealFrom(out JobRef job, out bool shouldRetry)
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
        bool ok = _worker.TryWakeUp();
        if (ok)
        {
            _statsWokeUpNotify++;
        }

        return ok;
    }

    public void Dispose()
    {
        _worker.Dispose();
    }

    public void RunJob16(ref RefList16<IJob> jobs)
    {
        if (jobs.Count == 0) return;
        if (jobs.Count == 1)
        {
            jobs[0]!.Execute(this);
            return;
        }


        RefList16<JobRef> jobLatches = new RefList16<JobRef>(jobs.Count - 1);

        if (jobs.Count > 1)
        {
            MultiPushJob(jobs.AsSpan()[1..], jobLatches.AsSpan());
        }
        /*
        for (int i = jobs.Count - 1; i > 0; i--)
        {
            // In reverse order
            jobLatches.Add(PushJob(jobs[i]!, false));
        }
        NotifyNewJob(jobLatches.Count);
        */

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

    public void PrintDebugStats()
    {
        long actualSteal = _statsSteal;
        long actualStealRetry = _statsStealRetry + _worker._statsExecuteStolenRetry;

        Console.Error.WriteLine($"{ContextIdx,5}  ST {actualSteal,5}  STR {actualStealRetry,5}  SF {_statsStolenFrom,5}  NJ {_statsNoJob,5}  W {_statsWokeUpNotify,5}  WY {_worker._statsYielded}  WW {_worker._statsWaited}");
    }
}

public interface IJob
{
    public void Execute(Context ctx);
}

public struct JobRef(IJob job, long id, ManualResetEventSlim _resetEvent)
{
    public long Id = id;

    public ManualResetEventSlim ResetEvent { get; } = _resetEvent;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteInline(Context context)
    {
        job.Execute(context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ExecuteNonInline(Context context)
    {
        // HOT PATH
        job.Execute(context);

        ResetEvent.Set();
    }
}
