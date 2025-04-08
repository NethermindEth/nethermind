// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;

namespace Nethermind.Core.Threading;

public class WorkStealingExecutor: IDisposable
{
    // To randomize stealing. Not exactly random, but it should work.
    private int _stealPartitionCounter = 0;

    private int _workerCount = 0;
    private Context[] _workerContexts;
    private ConcurrentQueue<JobRef> _incomingJob = new ConcurrentQueue<JobRef>();

    private bool[] _sleepingWorkersAdded; // Prevent duplicates in _sleepingContexts
    // fast pool of sleeping worker for waking up on new job.
    private ConcurrentQueue<int> _sleepingWorkers = new();

    public WorkStealingExecutor(int workerCount)
    {
        _workerCount = workerCount;
        _workerContexts = new Context[workerCount];
        _sleepingWorkersAdded = new bool[_workerCount];

        for (int i = 0; i < workerCount; i++)
        {
            _workerContexts[i] = new Context(this, i);
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
        jobRef.ResetEvent.Wait();
    }

    public bool TryStealJob(out JobRef job)
    {
        if (_incomingJob.TryDequeue(out job!))
        {
            return true;
        }

        int activeContexts = _workerCount;
        int stealPartitionCounter = Interlocked.Increment(ref _stealPartitionCounter);
        if (stealPartitionCounter > _workerCount)
        {
            Interlocked.Exchange(ref _stealPartitionCounter, stealPartitionCounter % _workerCount);
        }

        for (int i = 0; i < activeContexts; i++)
        {
            int idx = (stealPartitionCounter + 1) % _workerCount;
            Context context = _workerContexts[idx];
            // I guess hot path
            if (context.TryStealFrom(out job!))
            {
                return true;
            }
        }

        return false;
    }

    public void AddToSleepingWorker(Context context)
    {
        if (Interlocked.CompareExchange(ref _sleepingWorkersAdded[context.ContextIdx], true, false) == false)
        {
            _sleepingWorkers.Enqueue(context.ContextIdx);
        }
    }

    public void NotifyNewJob(int queueSize)
    {
        // HOT PATH
        // Tricky code...

        // TODO: Check if using a counter is faster
        int toWakeUp = queueSize;
        while (toWakeUp > 0)
        {
            if (!_sleepingWorkers.TryDequeue(out int ctxId))
            {
                break;
            }

            _sleepingWorkersAdded[ctxId] = false;

            if (_workerContexts[ctxId].TryWakeUp())
            {
                toWakeUp--;
            }
            else
            {
                // Note: Worker can wake up on its own without removing from this bag.
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

    public long CalculateTotalTimeAsleep()
    {
        long totalTime = 0;
        for (int i = 0; i < _workerCount; i++)
        {
            totalTime += _workerContexts[i].TotalTimeAsleep;
        }
        return totalTime;
    }
}

public class Worker: IDisposable
{
    private const int NoJobThreshold = 10;
    private const int LatchWaitMs = 100;
    private readonly Context _context;
    private readonly Thread _thread;
    private ManualResetEventSlim _finishLatch;
    private bool _isAsleep = false;
    private int _waitRound = 0;
    internal long TotalTimeAsleep = 0;

    public Worker(Context context)
    {
        _context = context;
        _finishLatch = new ManualResetEventSlim(false);
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
                if (_context.TryGetJob(out JobRef? otherRef))
                {
                    ResetWaitCounter();
                    otherRef!.ExecuteNonInline(_context);
                    continue;
                }

                OnNoJob(_finishLatch);
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
        _thread.Interrupt();
        return true;
    }

    public void ResetWaitCounter()
    {
        _waitRound = 0;
    }

    public void OnNoJob(ManualResetEventSlim latch)
    {
        _waitRound++;
        if (_waitRound > NoJobThreshold)
        {
            _isAsleep = true;
            _context.NotifyWorkerAsleep();
            long start = Stopwatch.GetTimestamp();
            try
            {
                latch.Wait(LatchWaitMs);
            }
            catch (ThreadInterruptedException)
            {
            }
            TotalTimeAsleep += Stopwatch.GetTimestamp() - start;
            _isAsleep = false;
            return;
        }
        Thread.Yield();
    }
}

public class Context: IDisposable
{
    private DStack<JobRef> _jobStack = new(16);
    private readonly WorkStealingExecutor _executor;
    private readonly int _contextIdx;
    private readonly Worker _worker;

    public Context(WorkStealingExecutor executor, int contextIdx)
    {
        _executor = executor;
        _contextIdx = contextIdx;
        _worker = new Worker(this);
    }

    public void StartWorker()
    {
        _worker.Start();
    }

    public int ContextIdx => _contextIdx;
    public long TotalTimeAsleep => _worker.TotalTimeAsleep;

    public void Fork(IJob job1, IJob job2)
    {
        JobRef job2Ref = new(job2, new ManualResetEventSlim());
        Push(job2Ref);
        job1.Execute(this);

        WaitForJobOrKeepBusy(job2Ref.ResetEvent);
    }

    private void WaitForJobOrKeepBusy(ManualResetEventSlim latch)
    {
        // Announce looking here
        _worker.ResetWaitCounter();

        while (!latch.IsSet)
        {
            if (_jobStack.TryPop(out JobRef? otherRef))
            {
                if (ReferenceEquals(latch, otherRef!.ResetEvent))
                {
                    // Inline execute
                    otherRef.ExecuteInline(this);
                    return;
                }

                otherRef!.ExecuteNonInline(this);
            }
            else if (_executor.TryStealJob(out otherRef))
            {
                otherRef!.ExecuteNonInline(this);
            }
            else
            {
                _worker.OnNoJob(latch);
            }
        }
    }

    private void Push(JobRef jobRef)
    {
        // HOT PATH
        _jobStack.Push(jobRef);
        _executor.NotifyNewJob(_jobStack.Count);
    }

    internal bool TryGetJob(out JobRef? jobRef)
    {
        if (_jobStack.TryPop(out jobRef))
        {
            return true;
        }

        if (_executor.TryStealJob(out jobRef))
        {
            return true;
        }

        return false;
    }

    public bool TryStealFrom(out JobRef job)
    {
        return _jobStack.TryDequeue(out job!);
    }

    public bool TryWakeUp()
    {
        return _worker.TryWakeUp();
    }

    public void NotifyWorkerAsleep()
    {
        _executor.AddToSleepingWorker(this);
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

public record JobRef(IJob job, ManualResetEventSlim ResetEvent)
{
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
