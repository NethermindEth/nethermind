// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
/// A <see cref="SemaphoreSlim"/> holds the slots and serves its waiters in arrival order; it alone decides between a grant
/// and a timeout or cancellation, so neither a slot nor a waiter is lost between them.
/// A slot that reaches a waiter after its wait has ended, as when the timer fires late, is passed on.
/// </remarks>
internal sealed class EvmAdmissionGate
{
    private const string BusyMessage = "All EVM execution slots are busy.";
    private const string WaitTimeoutMessage = "No EVM execution slot was granted within the queue wait budget.";

    private readonly SemaphoreSlim _slots;
    private readonly int _queueLimit;
    // Waiters, counted until they resume, so a granted waiter still holds its queue place for that moment.
    private int _queued;
    private long _queueFullRejections;
    private long _notQueueableRejections;
    private long _waitTimeoutRejections;
    private long _cancellations;
    private long _queuedGrants;
    private long _queueWaitMicroseconds;

    internal EvmAdmissionGate(IJsonRpcConfig config, TimeProvider? timeProvider = null)
    {
        TimeProvider = timeProvider ?? TimeProvider.System;
        Permits = config.GetEvmExecutionSlots();
        _slots = new SemaphoreSlim(Permits, Permits);
        Budget = TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
        _queueLimit = config.EvmExecutionQueueLimit;
    }

    /// <summary>The most requests that hold a slot at once.</summary>
    internal int Permits { get; }
    internal TimeSpan Budget { get; }
    internal TimeProvider TimeProvider { get; }
    internal int InFlight => Permits - _slots.CurrentCount;
    internal int Queued => Volatile.Read(ref _queued);

    // This gate's own share of the RpcAdmission counters in Metrics, which every gate in the process adds to.
    internal long QueueFullRejections => Interlocked.Read(ref _queueFullRejections);
    internal long NotQueueableRejections => Interlocked.Read(ref _notQueueableRejections);
    internal long WaitTimeoutRejections => Interlocked.Read(ref _waitTimeoutRejections);
    internal long Cancellations => Interlocked.Read(ref _cancellations);
    internal long QueuedGrants => Interlocked.Read(ref _queuedGrants);
    internal long QueueWaitMicroseconds => Interlocked.Read(ref _queueWaitMicroseconds);

    /// <summary>Acquires an execution slot, waiting up to <paramref name="maxWait"/> for one if every slot is busy.</summary>
    /// <param name="maxWait">How long the request may wait for a slot, capped at <see cref="Budget"/>; zero or less rejects it at once.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>A lease to dispose once, after the execution, including any task it returned, has completed.</returns>
    /// <exception cref="LimitExceededException">No slot was free and the request could not queue.</exception>
    /// <exception cref="WaitTimeoutException">No slot was granted within <paramref name="maxWait"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a slot was granted.</exception>
    internal async ValueTask<Lease> AdmitAsync(TimeSpan maxWait, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_slots.Wait(0))
        {
            Metrics.ChangeRpcAdmissionInFlight(1);
            return new Lease(this);
        }

        if (maxWait > Budget) maxWait = Budget;
        if (maxWait <= TimeSpan.Zero)
        {
            Interlocked.Increment(ref _notQueueableRejections);
            Metrics.IncrementRpcAdmissionNotQueueableRejections();
            throw new LimitExceededException(BusyMessage);
        }

        int queued = Interlocked.Increment(ref _queued);
        if (_queueLimit > 0 && queued > _queueLimit)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _queueFullRejections);
            Metrics.IncrementRpcAdmissionQueueFullRejections();
            throw new LimitExceededException(BusyMessage);
        }

        Metrics.ChangeRpcAdmissionQueued(1);
        long queuedAt = TimeProvider.GetTimestamp();
        try
        {
            using CancellationTokenSource timeout = new(maxWait, TimeProvider);
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            await _slots.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _cancellations);
            Metrics.IncrementRpcAdmissionCancellations();
            throw;
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _waitTimeoutRejections);
            Metrics.IncrementRpcAdmissionWaitTimeoutRejections();
            throw new WaitTimeoutException();
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
            Metrics.ChangeRpcAdmissionQueued(-1);
        }

        TimeSpan waited = TimeProvider.GetElapsedTime(queuedAt);
        // The timer that ends the wait runs on the thread pool and fires late when the pool is busy, so a slot can arrive
        // after the wait has ended. Pass it on: a request past its wait never runs.
        if (waited >= maxWait)
        {
            _slots.Release();
            Interlocked.Increment(ref _waitTimeoutRejections);
            Metrics.IncrementRpcAdmissionWaitTimeoutRejections();
            throw new WaitTimeoutException();
        }

        long waitedMicroseconds = waited.Ticks / TimeSpan.TicksPerMicrosecond;
        Interlocked.Increment(ref _queuedGrants);
        Interlocked.Add(ref _queueWaitMicroseconds, waitedMicroseconds);
        Metrics.AddRpcAdmissionQueuedGrant(waitedMicroseconds);
        Metrics.ChangeRpcAdmissionInFlight(1);
        return new Lease(this);
    }

    // Hands the slot straight to the oldest waiter, if any. The in-flight gauge dips by one until that waiter resumes.
    private void Release()
    {
        _slots.Release();
        Metrics.ChangeRpcAdmissionInFlight(-1);
    }

    /// <summary>A queued request got no slot within its wait.</summary>
    internal sealed class WaitTimeoutException() : LimitExceededException(WaitTimeoutMessage);

    /// <summary>An execution slot; disposing it passes the slot to the next waiter or frees it.</summary>
    /// <remarks>
    /// A second dispose of the same lease does nothing. Disposing a copy of a disposed lease releases once more: while other
    /// leases are held, that admits one request too many until they are released, and with every slot free it throws
    /// <see cref="SemaphoreFullException"/>.
    /// </remarks>
    internal struct Lease(EvmAdmissionGate? gate) : IDisposable
    {
        private EvmAdmissionGate? _gate = gate;

        internal readonly bool IsGranted => _gate is not null;

        public void Dispose()
        {
            EvmAdmissionGate? gate = _gate;
            _gate = null;
            gate?.Release();
        }
    }
}
