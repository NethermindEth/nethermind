// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;

namespace Nethermind.Core.Threading;

// Its like rust's rayon, but worst.
public class WorkStealingExecutor: IDisposable
{
    // To randomize stealing. Not exactly random, but it should work.
    private int _stealPartitionCounter = 0;

    private int _workerCount = 0;
    private int _asleepWorker = 0;
    private Context[] _workerContexts;
    private ConcurrentQueue<JobRef> _incomingJob = new ConcurrentQueue<JobRef>();

    private bool[] _sleepingWorkersAdded; // Prevent duplicates in _sleepingContexts
    // fast pool of sleeping worker for waking up on new job.
    private ConcurrentQueue<int> _sleepingWorkers = new();

    public WorkStealingExecutor(int workerCount, int initialStackSize)
    {
        _workerCount = workerCount;
        _workerContexts = new Context[workerCount];
        _sleepingWorkersAdded = new bool[workerCount];

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
        if (_incomingJob.TryDequeue(out job!))
        {
            return true;
        }

        int activeContexts = _workerCount;
        int ctxToCheckFirst = Interlocked.Increment(ref _stealPartitionCounter);
        if (ctxToCheckFirst > activeContexts)
        {
            Interlocked.Exchange(ref _stealPartitionCounter, ctxToCheckFirst % activeContexts);
        }

        // Main bottleneck
        for (int i = 0; i < activeContexts; i++)
        {
            int idx = (ctxToCheckFirst + i) % activeContexts;
            Context context = _workerContexts[idx];
            // I guess hot path
            if (context.TryStealFrom(out job!, out bool contextSaidShouldRetry))
            {
                return true;
            }

            if (contextSaidShouldRetry) shouldRetry = true;
        }

        return false;
    }

    public void AddToSleepingWorker(Context context)
    {
        if (Interlocked.CompareExchange(ref _sleepingWorkersAdded[context.ContextIdx], true, false) == false)
        {
            _sleepingWorkers.Enqueue(context.ContextIdx);
            Interlocked.Increment(ref _asleepWorker);
        }
    }

    public void RemoveFromSleepingWorker(Context context)
    {
        Interlocked.Decrement(ref _asleepWorker);

        // Woken up via notification
        if (Interlocked.Exchange(ref _sleepingWorkersAdded[context.ContextIdx], false) == false) return;

        while (_sleepingWorkers.TryDequeue(out int ctxId))
        {
            if (_sleepingWorkersAdded[ctxId])
            {
                // Queue it back
                _sleepingWorkers.Enqueue(ctxId);
            }
            if (ctxId == context.ContextIdx) break;
        }
    }

    public void NotifyNewJob()
    {
        // HOT PATH
        // Tricky code...

        if (_asleepWorker == 0) return;

        while (true)
        {
            if (!_sleepingWorkers.TryDequeue(out int ctxId))
            {
                break;
            }

            if (!Interlocked.Exchange(ref _sleepingWorkersAdded[ctxId], false)) continue; // Woke up on its own

            if (_workerContexts[ctxId].TryWakeUp())
            {
                return;
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
    private readonly Context _context;
    private readonly Thread _thread;
    private ManualResetEventSlim _finishLatch;
    private ManualResetEventSlim _sleepLatch;
    private bool _isAsleep = false;
    private int _waitRound = 0;

    public Worker(Context context)
    {
        _context = context;
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
                if (_context.TryGetJob(out JobRef otherRef, out bool shouldRetry))
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
        if (!_isAsleep) return false;
        _sleepLatch.Set();
        return true;
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
            _isAsleep = true;
            _context.NotifyWorkerAsleep();
            try
            {
                latch.Wait(LatchWaitMs);
            }
            catch (ThreadInterruptedException)
            {
            }
            _context.NotifyWorkerAwake();
            _isAsleep = false;
            _waitRound = 0;
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
        _worker = new Worker(this);
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

    internal bool TryGetJob(out JobRef jobRef, out bool shouldRetry)
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

    public void NotifyWorkerAsleep()
    {
        _executor.AddToSleepingWorker(this);
    }

    public void NotifyWorkerAwake()
    {
        _executor.RemoveFromSleepingWorker(this);
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

    public void ExecuteInline(Context context)
    {
        job.Execute(context);
    }

    public void ExecuteNonInline(Context context)
    {
        // HOT PATH
        job.Execute(context);

        ResetEvent.Set();
    }
}
