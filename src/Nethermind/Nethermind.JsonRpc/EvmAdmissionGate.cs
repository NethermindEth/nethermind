// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc;

/// <summary>
/// Runs at most <see cref="IJsonRpcConfig.EthModuleConcurrentInstances"/> EVM-executing JSON-RPC requests
/// (<see cref="Modules.JsonRpcMethodAttribute.IsEvmExecution"/>) at a time, queueing the excess for at most
/// <see cref="IJsonRpcConfig.EvmExecutionMaxQueueWaitMs"/>.
/// </summary>
/// <remarks>
/// EVM throughput plateaus at about one execution per logical processor, so running more at once only adds latency and,
/// past saturation, wastes work on requests that are rejected anyway. An explicitly configured pool size is used as is;
/// queueing and shedding start only when more than that many requests are in flight, so a pool at or above the peak
/// concurrency turns them off. Waiters are served in order of arrival.
/// </remarks>
internal sealed class EvmAdmissionGate
{
    private const string BusyMessage = "All EVM execution slots are busy.";
    private const string WaitTimeoutMessage = "No EVM execution slot was granted within the queue wait budget.";

    private readonly Lock _lock = new();
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _budget;
    private readonly int _queueLimit;
    private int _inFlight;

    internal EvmAdmissionGate(IJsonRpcConfig config, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Permits = config.GetEvmExecutionSlots();
        _budget = TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
        _queueLimit = Math.Max(0, config.EvmExecutionQueueLimit);
    }

    /// <summary>The most requests that hold a slot at once.</summary>
    internal int Permits { get; }
    internal TimeSpan Budget => _budget;
    internal TimeProvider TimeProvider => _timeProvider;
    internal int InFlight => Volatile.Read(ref _inFlight);

    internal int Queued
    {
        get
        {
            lock (_lock)
            {
                return _waiters.Count;
            }
        }
    }

    // This gate's own share of the RpcAdmission counters in Metrics, which every gate in the process adds to.
    internal long QueueFullRejections { get; private set; }
    internal long NotQueueableRejections { get; private set; }
    internal long WaitTimeoutRejections { get; private set; }
    internal long Cancellations { get; private set; }
    internal long QueuedGrants { get; private set; }
    internal long QueueWaitMicroseconds { get; private set; }

    /// <summary>Acquires an execution slot, waiting up to <paramref name="maxWait"/> for one if every slot is busy.</summary>
    /// <param name="maxWait">How long the request may wait for a slot, capped at <see cref="Budget"/>; zero or less rejects it at once.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>A lease to dispose exactly once, after the execution, including any task it returned, has completed.</returns>
    /// <exception cref="LimitExceededException">No slot was free and the request could not queue.</exception>
    /// <exception cref="WaitTimeoutException">No slot was granted within <paramref name="maxWait"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was granted.</exception>
    internal async ValueTask<Lease> AdmitAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Waiter waiter;
        lock (_lock)
        {
            if (_inFlight < Permits)
            {
                Metrics.RpcAdmissionInFlight = ++_inFlight;
                return new Lease(this);
            }

            if (maxWait > _budget) maxWait = _budget;
            if (maxWait <= TimeSpan.Zero)
            {
                Metrics.RpcAdmissionNotQueueableRejections++;
                NotQueueableRejections++;
                throw new LimitExceededException(BusyMessage);
            }

            if (_queueLimit > 0 && _waiters.Count >= _queueLimit)
            {
                Metrics.RpcAdmissionQueueFullRejections++;
                QueueFullRejections++;
                throw new LimitExceededException(BusyMessage);
            }

            waiter = new Waiter(_timeProvider.GetTimestamp(), maxWait);
            _waiters.AddLast(waiter.Node);
            Metrics.RpcAdmissionQueued = _waiters.Count;
        }

        Lease lease;
        try
        {
            lease = await waiter.Task.WaitAsync(waiter.MaxWait, _timeProvider, cancellationToken);
        }
        catch (Exception ex)
        {
            if (TryRemove(waiter, ex))
            {
                if (ex is TimeoutException) throw new WaitTimeoutException();
                throw;
            }

            // Release dequeued the waiter first, so its decision stands.
            lease = await waiter.Task;
        }

        return lease.IsGranted ? lease : throw new WaitTimeoutException();
    }

    private bool TryRemove(Waiter waiter, Exception reason)
    {
        lock (_lock)
        {
            if (waiter.Node.List is null)
            {
                return false;
            }

            _waiters.Remove(waiter.Node);
            Metrics.RpcAdmissionQueued = _waiters.Count;
            // Any other failure is rethrown and answered as an internal error, so it counts as neither.
            switch (reason)
            {
                case TimeoutException:
                    Metrics.RpcAdmissionWaitTimeoutRejections++;
                    WaitTimeoutRejections++;
                    break;
                case OperationCanceledException:
                    Metrics.RpcAdmissionCancellations++;
                    Cancellations++;
                    break;
            }

            return true;
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            Debug.Assert(_inFlight > 0, "a lease was released twice");
            // Waiters resume on the thread pool, so completing them under the lock never runs their continuations here.
            while (_waiters.First is { Value: Waiter next })
            {
                _waiters.RemoveFirst();
                Metrics.RpcAdmissionQueued = _waiters.Count;
                TimeSpan waited = _timeProvider.GetElapsedTime(next.EnqueuedTimestamp);
                // Its timeout may not have fired yet, but a waiter past its budget must not be admitted.
                if (waited < next.MaxWait)
                {
                    long waitedMicroseconds = waited.Ticks / TimeSpan.TicksPerMicrosecond;
                    Metrics.RpcAdmissionQueuedGrants++;
                    QueuedGrants++;
                    Metrics.RpcAdmissionQueueWaitMicroseconds += waitedMicroseconds;
                    QueueWaitMicroseconds += waitedMicroseconds;
                    next.SetResult(new Lease(this));
                    return;
                }

                Metrics.RpcAdmissionWaitTimeoutRejections++;
                WaitTimeoutRejections++;
                next.SetResult(default);
            }

            Metrics.RpcAdmissionInFlight = --_inFlight;
        }
    }

    /// <summary>A queued request got no slot within its wait.</summary>
    internal sealed class WaitTimeoutException() : LimitExceededException(WaitTimeoutMessage);

    /// <summary>An execution slot; disposing it passes the slot to the next waiter or frees it.</summary>
    /// <remarks>Dispose it exactly once: a second release would permanently raise the number of concurrent executions.</remarks>
    internal readonly struct Lease(EvmAdmissionGate? gate) : IDisposable
    {
        internal bool IsGranted => gate is not null;

        public void Dispose() => gate?.Release();
    }

    /// <summary>A queued admission, completed only by <see cref="Release"/> after dequeuing it.</summary>
    private sealed class Waiter : TaskCompletionSource<Lease>
    {
        public Waiter(long enqueuedTimestamp, TimeSpan maxWait) : base(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            EnqueuedTimestamp = enqueuedTimestamp;
            MaxWait = maxWait;
            Node = new LinkedListNode<Waiter>(this);
        }

        public long EnqueuedTimestamp { get; }
        public TimeSpan MaxWait { get; }

        /// <summary>The waiter's place in the queue, so leaving it early is O(1); detached once it has left.</summary>
        public LinkedListNode<Waiter> Node { get; }
    }
}
