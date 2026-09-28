// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
/// concurrency turns them off. Priority waiters are served first, in order of arrival. The others are served in order of
/// arrival plus a penalty that grows with their <c>params</c> size up to half of what they may wait, so a smaller request
/// overtakes a larger one that arrived shortly before it. A waiter that has waited half the budget is served before any
/// later arrival without priority, so sustained light traffic cannot starve a heavy request.
/// </remarks>
internal sealed class EvmAdmissionGate
{
    internal const int MaxWeight = 8;
    internal const int BytesPerWeightUnit = 128 * 1024;

    private const long PriorityOrder = long.MinValue;
    private const string BusyMessage = "All EVM execution slots are busy.";
    private const string WaitTimeoutMessage = "No EVM execution slot was granted within the queue wait budget.";

    private readonly Lock _lock = new();
    private readonly PriorityQueue<Waiter, (long Order, long Sequence)> _waiters = new();
    private readonly LinkedList<Waiter> _arrivals = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _budget;
    private readonly long _weightPenalty;
    private readonly int _queueLimit;
    private long _sequence;
    private int _inFlight;

    internal EvmAdmissionGate(IJsonRpcConfig config, TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        Permits = Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);
        _budget = TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
        _queueLimit = Math.Max(0, config.EvmExecutionQueueLimit);
        _weightPenalty = (long)(_budget.TotalSeconds * _timeProvider.TimestampFrequency / (2 * (MaxWeight - 1)));
    }

    internal int Permits { get; }
    internal TimeSpan Budget => _budget;
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

    /// <summary>Converts the byte length of a request's raw <c>params</c> into a weight from 1 to <see cref="MaxWeight"/>.</summary>
    internal static int Weigh(int paramsUtf8Length) => Math.Min(MaxWeight, 1 + paramsUtf8Length / BytesPerWeightUnit);

    /// <summary>Acquires an execution slot, waiting up to <paramref name="maxWait"/> for one if every slot is busy.</summary>
    /// <param name="paramsUtf8Length">Byte length of the request's raw <c>params</c>; see <see cref="Weigh"/>.</param>
    /// <param name="maxWait">How long the request may wait for a slot, capped at <see cref="Budget"/>; zero or less rejects it at once.</param>
    /// <param name="priority">Serves the request ahead of every waiter without priority.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>A lease to dispose exactly once, after the execution, including any task it returned, has completed.</returns>
    /// <exception cref="LimitExceededException">No slot was free and the request could not queue, or none was granted within <paramref name="maxWait"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was granted.</exception>
    internal async ValueTask<Lease> AdmitAsync(int paramsUtf8Length, TimeSpan maxWait, bool priority, CancellationToken cancellationToken)
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

            long now = _timeProvider.GetTimestamp();
            waiter = new Waiter(now, maxWait);
            _waiters.Enqueue(waiter, (priority ? PriorityOrder : now + SizePenalty(paramsUtf8Length, maxWait), ++_sequence));
            waiter.Arrival = _arrivals.AddLast(waiter);
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
                if (ex is TimeoutException) throw new LimitExceededException(WaitTimeoutMessage);
                throw;
            }

            // Release dequeued the waiter first, so its decision stands.
            lease = await waiter.Task;
        }

        return lease.IsGranted ? lease : throw new LimitExceededException(WaitTimeoutMessage);
    }

    // Capped at half the waiter's own wait: a batch item may wait less than half the budget, so it could never age to the
    // front, but it still goes ahead of anything that arrives that much after it.
    private long SizePenalty(int paramsUtf8Length, TimeSpan maxWait) =>
        Math.Min((Weigh(paramsUtf8Length) - 1) * _weightPenalty, (long)(maxWait.TotalSeconds * _timeProvider.TimestampFrequency / 2));

    private bool TryRemove(Waiter waiter, Exception reason)
    {
        lock (_lock)
        {
            if (!_waiters.Remove(waiter, out _, out _))
            {
                return false;
            }

            _arrivals.Remove(waiter.Arrival!);
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
            while (TryDequeue(out Waiter? next))
            {
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

    // Caller holds _lock. Takes the smallest order, unless the oldest waiter has waited half the budget and no priority
    // waiter is queued: then the oldest goes first.
    private bool TryDequeue([NotNullWhen(true)] out Waiter? next)
    {
        next = _arrivals.First?.Value;
        if (next is null)
        {
            return false;
        }

        if (_waiters.TryPeek(out _, out (long Order, long Sequence) first) && first.Order != PriorityOrder
            && _timeProvider.GetElapsedTime(next.EnqueuedTimestamp) >= _budget / 2)
        {
            _waiters.Remove(next, out _, out _);
        }
        else
        {
            next = _waiters.Dequeue();
        }

        _arrivals.Remove(next.Arrival!);
        return true;
    }

    /// <summary>An execution slot; disposing it passes the slot to the next waiter or frees it.</summary>
    /// <remarks>Dispose it exactly once: a second release would permanently raise the number of concurrent executions.</remarks>
    internal readonly struct Lease(EvmAdmissionGate? gate) : IDisposable
    {
        internal bool IsGranted => gate is not null;

        public void Dispose() => gate?.Release();
    }

    /// <summary>A queued admission, completed only by <see cref="Release"/> after dequeuing it.</summary>
    private sealed class Waiter(long enqueuedTimestamp, TimeSpan maxWait) : TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
        public TimeSpan MaxWait { get; } = maxWait;
        public LinkedListNode<Waiter>? Arrival;
    }
}
