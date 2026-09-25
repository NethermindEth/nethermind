// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
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
/// past saturation, wastes work on requests that are rejected anyway. Waiters are served in order of arrival plus a penalty
/// that grows with their <c>params</c> size up to half the wait budget, so a smaller request overtakes a larger one that
/// arrived shortly before it. A waiter that has waited half the budget is served before any later arrival, so sustained
/// light traffic cannot starve a heavy request.
/// </remarks>
internal sealed class EvmAdmissionGate
{
    internal const int MaxWeight = 8;
    internal const int BytesPerWeightUnit = 128 * 1024;

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

    /// <summary>Converts the byte length of a request's raw <c>params</c> into a weight from 1 to <see cref="MaxWeight"/>.</summary>
    internal static int Weigh(int paramsUtf8Length) => Math.Min(MaxWeight, 1 + paramsUtf8Length / BytesPerWeightUnit);

    /// <summary>Acquires an execution slot, waiting for one if every slot is busy and <paramref name="allowQueue"/> is set.</summary>
    /// <param name="paramsUtf8Length">Byte length of the request's raw <c>params</c>; see <see cref="Weigh"/>.</param>
    /// <param name="allowQueue">Whether the request may wait for a slot rather than be rejected at once.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>A lease to dispose exactly once, after the execution, including any task it returned, has completed.</returns>
    /// <exception cref="LimitExceededException">No slot was free and the request could not queue, or none was granted within the budget.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was granted.</exception>
    internal async ValueTask<Lease> AdmitAsync(int paramsUtf8Length, bool allowQueue, CancellationToken cancellationToken)
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

            if (!allowQueue || _budget == TimeSpan.Zero || _queueLimit > 0 && _waiters.Count >= _queueLimit)
            {
                Metrics.RpcAdmissionImmediateRejections++;
                throw new LimitExceededException(BusyMessage);
            }

            long now = _timeProvider.GetTimestamp();
            waiter = new Waiter(now);
            _waiters.Enqueue(waiter, (now + (Weigh(paramsUtf8Length) - 1) * _weightPenalty, ++_sequence));
            waiter.Arrival = _arrivals.AddLast(waiter);
            Metrics.RpcAdmissionQueued = _waiters.Count;
        }

        Lease lease;
        try
        {
            lease = await waiter.Task.WaitAsync(_budget, _timeProvider, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            if (TryRemove(waiter, timedOut: ex is TimeoutException))
            {
                if (ex is OperationCanceledException) throw;
                throw new LimitExceededException(WaitTimeoutMessage);
            }

            // Release dequeued the waiter first, so its decision stands.
            lease = await waiter.Task;
        }

        return lease.IsGranted ? lease : throw new LimitExceededException(WaitTimeoutMessage);
    }

    private bool TryRemove(Waiter waiter, bool timedOut)
    {
        lock (_lock)
        {
            if (!_waiters.Remove(waiter, out _, out _))
            {
                return false;
            }

            _arrivals.Remove(waiter.Arrival!);
            Metrics.RpcAdmissionQueued = _waiters.Count;
            if (timedOut) Metrics.RpcAdmissionWaitTimeoutRejections++;
            else Metrics.RpcAdmissionCancellations++;
            return true;
        }
    }

    private void Release()
    {
        lock (_lock)
        {
            // Waiters resume on the thread pool, so completing them under the lock never runs their continuations here.
            while (TryDequeue(out Waiter? next))
            {
                Metrics.RpcAdmissionQueued = _waiters.Count;
                // Its timeout may not have fired yet, but a waiter past its budget must not be admitted.
                if (_timeProvider.GetElapsedTime(next.EnqueuedTimestamp) < _budget)
                {
                    next.SetResult(new Lease(this));
                    return;
                }

                Metrics.RpcAdmissionWaitTimeoutRejections++;
                next.SetResult(default);
            }

            Metrics.RpcAdmissionInFlight = --_inFlight;
        }
    }

    // Caller holds _lock. Takes the smallest order, unless the oldest waiter has waited half the budget: then it goes first.
    private bool TryDequeue([NotNullWhen(true)] out Waiter? next)
    {
        next = _arrivals.First?.Value;
        if (next is null)
        {
            return false;
        }

        if (_timeProvider.GetElapsedTime(next.EnqueuedTimestamp) >= _budget / 2)
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
    private sealed class Waiter(long enqueuedTimestamp) : TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
        public LinkedListNode<Waiter>? Arrival;
    }
}
