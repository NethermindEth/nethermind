// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nethermind.Core.Threading;

// Its like rust's rayon, but worst.
public class WorkStealingExecutor: IDisposable
{
    private readonly int _workerCount = 0;

    private int _asleepWorker = 0;
    private uint _roundRobinStealIdx = 0;
    private uint _wakeUpCounter = 0;

    private readonly Context[] _workerContexts;
    private readonly ConcurrentQueue<JobRef> _incomingJob = new ConcurrentQueue<JobRef>();

    public WorkStealingExecutor(int workerCount, int initialStackSize)
    {
        _workerCount = workerCount;
        _workerContexts = new Context[workerCount];

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
        JobRef jobRef = new JobRef(job, new ManualResetEventSlim());
        _incomingJob.Enqueue(jobRef);
        NotifyNewJob();
        jobRef.ResetEvent.Wait();
    }

    public bool TryStealJob(out JobRef job, out bool shouldRetry)
    {
        shouldRetry = false;
        int activeContexts = _workerCount;
        for (int i = 0; i < activeContexts; i++)
        {
            int idx = (int)(Interlocked.Increment(ref _roundRobinStealIdx) % activeContexts);
            Context context = _workerContexts[idx];
            // I guess hot path
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

    public void AddToSleepingWorker()
    {
        Interlocked.Increment(ref _asleepWorker);
    }

    public void RemoveFromSleepingWorker()
    {
        Interlocked.Decrement(ref _asleepWorker);
    }

    public void NotifyNewJob()
    {
        // HOT PATH
        // Tricky code...

        if (_asleepWorker == 0) return;

        for (int i = 0; i < _workerCount; i++)
        {
            Context context = _workerContexts[Interlocked.Increment(ref _wakeUpCounter) % _workerCount];
            if (context.TryWakeUp())
            {
                break;
            }
        }
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
    private const int LatchWaitMs = 50;

    private readonly WorkStealingExecutor _executor;
    private readonly Context _context;
    private readonly Thread _thread;

    private ManualResetEventSlim _finishLatch;
    private ManualResetEventSlim _sleepLatch;
    private bool _isAsleep = false;
    private int _waitRound = 0;

    public Worker(Context context, WorkStealingExecutor executor)
    {
        _context = context;
        _executor = executor;
        _finishLatch = new ManualResetEventSlim(false);
        _sleepLatch = new ManualResetEventSlim(false);
        _thread = new Thread(WorkerLoop);
    }

    public void Start()
    {
        _thread.Start();
    }

    private void WorkerLoop()
    {
        try
        {
            while (!_finishLatch.IsSet)
            {
                if (_context.TryGetJob(out JobRef otherRef, out bool shouldRetry, _finishLatch))
                {
                    ResetWaitCounter();
                    otherRef.ExecuteNonInline(_context);
                    continue;
                }

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
            _executor.RemoveFromSleepingWorker();
            _sleepLatch.Set();
            return true;
        }

        return false;
    }

    public void ResetWaitCounter()
    {
        _waitRound = 0;
    }

    public void OnNoJob(ManualResetEventSlim latch)
    {
        _waitRound++;
        if (_waitRound > RoundUntilSleep)
        {
            if (Interlocked.CompareExchange(ref _isAsleep, true, false) == false)
            {
                _executor.AddToSleepingWorker();
            }

            latch.Wait(LatchWaitMs);

            if (Interlocked.CompareExchange(ref _isAsleep, false, true) == true)
            {
                _executor.RemoveFromSleepingWorker();
            }
            return;
        }

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
        if (!_latchPool.TryPop(out ManualResetEventSlim? resetEvent))
        {
            resetEvent = new ManualResetEventSlim(false);
        }
        else
        {
            resetEvent.Reset();
        }

        JobRef job2Ref = new(job2, resetEvent);
        Push(job2Ref);
        job1.Execute(this);

        WaitForJobOrKeepBusy(job2Ref.ResetEvent);
    }

    public ManualResetEventSlim PushJob(IJob job)
    {
        if (!_latchPool.TryPop(out ManualResetEventSlim? resetEvent))
        {
            resetEvent = new ManualResetEventSlim(false);
        }
        else
        {
            resetEvent.Reset();
        }

        JobRef jobRef = new(job, resetEvent);
        Push(jobRef);

        return resetEvent;
    }

    public void WaitForJobOrKeepBusy(ManualResetEventSlim latch)
    {
        // Announce looking here
        _worker.ResetWaitCounter();

        while (!latch.IsSet)
        {
            if (_jobStack.TryPop(out JobRef otherRef))
            {
                if (ReferenceEquals(latch, otherRef.ResetEvent))
                {
                    // Inline execute
                    otherRef.ExecuteInline(this);
                    // Recycle latcch
                    _latchPool.Push(latch);
                    return;
                }

                otherRef!.ExecuteNonInline(this);
            }
            else if (_executor.TryStealJob(out otherRef, out bool shouldRetry))
            {
                otherRef!.ExecuteNonInline(this);
            }
            else
            {
                if (!shouldRetry) _worker.OnNoJob(latch);
            }
        }

        // Recycle latcch
        _latchPool.Push(latch);
    }

    private void Push(JobRef jobRef)
    {
        // HOT PATH
        _jobStack.Push(jobRef);
        _executor.NotifyNewJob();
    }

    internal bool TryGetJob(out JobRef jobRef, out bool shouldRetry, ManualResetEventSlim latch)
    {
        if (_jobStack.TryPop(out jobRef))
        {
            shouldRetry = false;
            return true;
        }

        if (_executor.TryStealJob(out jobRef, out shouldRetry))
        {
            return true;
        }

        return false;
    }

    public bool TryStealFrom(out JobRef job, out bool shouldRetry)
    {
        return _jobStack.TryDequeue(out job!, out shouldRetry);
    }

    public bool TryWakeUp()
    {
        return _worker.TryWakeUp();
    }

    public void Dispose()
    {
        _worker.Dispose();
    }
}

public interface IJob
{
    public void Execute(Context ctx);
}

public struct JobRef(IJob job, ManualResetEventSlim _resetEvent)
{
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
