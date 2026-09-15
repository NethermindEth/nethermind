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
/// Admits EVM-executing JSON-RPC requests (those flagged <see cref="Modules.JsonRpcMethodAttribute.IsEvmExecution"/>):
/// a fixed number of permits, a bounded cost-ordered wait for the rest, and load shedding beyond that.
/// </summary>
/// <remarks>
/// Throughput of EVM-bound methods plateaus at roughly one execution per logical processor, and the override-environment
/// pool they execute in rejects instantly at that count; admitting more only converts throughput into queueing delay and,
/// past saturation, into work wasted on requests that are rejected anyway. The gate keeps concurrency at the plateau
/// (the configured EVM module concurrency) and turns the excess into fast "Too many requests" answers: a request that
/// finds no free permit waits asynchronously for at most <see cref="IJsonRpcConfig.EvmExecutionMaxQueueWaitMs"/>, and is
/// rejected up front once <see cref="MaxQueueDepthPerPermit"/> requests per permit are already waiting. A zero wait
/// budget disables queueing: the request is rejected on the calling thread without allocating a waiter.
/// <para>
/// Waiters are ordered by a deadline of <c>arrival + weight * quantum</c>, earliest first. Cheap requests therefore
/// overtake expensive ones, which is what keeps short calls moving under overload, but only those arriving within
/// <c>(weight - 1) * quantum</c> of a heavier request may pass it: after that window every newer arrival carries a later
/// deadline, so sustained light traffic cannot starve a heavy request indefinitely. With <see cref="QuantumsPerBudget"/>
/// quanta per budget the heaviest class can be overtaken for just under half its budget. "Heavy" means large, not
/// expensive: the weight is the <c>params</c> size (see <see cref="Weigh"/>).
/// </para>
/// <para>
/// Authenticated callers - the consensus client on the engine port, and every IPC request - are queued at the head
/// rather than shed: a saturated gate that refused them while still serving anonymous callers after a wait would be
/// starving the one caller the node exists to answer. No anonymous arrival can overtake one.
/// </para>
/// <para>
/// One lock guards the permits and the queue; it is held for a few instructions per admission and nothing under it calls
/// out. Expiry needs no timer of its own: each waiter arms a cancellation for its budget, and a settled waiter is skipped
/// when a permit reaches it. A permit is only ever handed straight to the next live waiter or returned to the free pool,
/// so it can neither leak into a caller that has gone nor sit idle behind a queue. Disposing the gate answers everyone
/// waiting as shed and rejects later arrivals.
/// </para>
/// </remarks>
internal sealed class EvmAdmissionGate : IDisposable
{
    internal const int MinWeight = 1;
    internal const int MaxWeight = 8;
    // Hex-encoded JSON is roughly twice the size of the override bytes it carries.
    internal const int BytesPerWeightUnit = 128 * 1024;
    // Puts the heaviest class's overtaking window at (MaxWeight - 1) / (2 * MaxWeight) of the budget - just under half,
    // the point at which the bucketed predecessor gave an aged waiter priority - so the measured ordering is preserved.
    internal const int QuantumsPerBudget = 2 * MaxWeight;
    internal const int MaxQueueDepthPerPermit = 8;

    // The text never leaves the process: ReturnErrorResponse answers every LimitExceededException with "Too many requests".
    private const string QueueFullMessage = "All EVM execution slots are busy and the request queue is full.";
    private const string QueueingDisabledMessage = "All EVM execution slots are busy and queueing is disabled.";
    private const string WaitTimeoutMessage = "Not granted an EVM execution slot within the queue wait budget.";
    private const string ShuttingDownMessage = "The node is shutting down.";

    private readonly Lock _lock = new();
    // Keyed on (deadline, sequence): PriorityQueue is not a stable heap, so equal deadlines need the sequence to keep
    // arrival order within a weight.
    private readonly PriorityQueue<Waiter, (long Deadline, long Sequence)> _waiters = new();
    private readonly List<(Waiter Waiter, (long Deadline, long Sequence) Key)> _rebuildScratch = [];
    private readonly Func<long> _timestamp;
    private readonly TimeSpan _budget;
    private readonly long _quantumTicks;
    private readonly int _maxWaiters;
    private long _sequence;
    private int _freePermits;
    // Waiters still waiting. Distinct from _waiters.Count, which also holds entries already settled by expiry or
    // cancellation while something live sits ahead of them; counting those would let corpses fill the depth cap.
    private int _liveWaiters;
    private bool _closed;
    // Lock-free mirror of _closed for the post-wait path, which runs outside the lock.
    private bool _closedFlag;

    /// <summary>Creates a gate sized from the EVM module concurrency and wait budget in <paramref name="config"/>.</summary>
    /// <param name="config">Supplies the permit count and the wait budget.</param>
    /// <param name="timestamp">Monotonic tick source; tests supply one so the ordering window is deterministic.</param>
    internal EvmAdmissionGate(IJsonRpcConfig config, Func<long>? timestamp = null)
    {
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        Permits = _freePermits = Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);
        _maxWaiters = Permits * MaxQueueDepthPerPermit;
        _budget = TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
        // A sub-quantum budget floors this to zero and the ordering collapses to arrival, which is right: there is no
        // room to reorder inside a budget that short.
        _quantumTicks = (long)(_budget.TotalSeconds * Stopwatch.Frequency) / QuantumsPerBudget;
    }

    internal int Permits { get; }
    internal int Queued => Volatile.Read(ref _liveWaiters);
    internal int InFlight
    {
        get
        {
            lock (_lock)
            {
                return Permits - _freePermits;
            }
        }
    }

    /// <summary>
    /// Converts the byte length of a request's raw <c>params</c> into its admission weight: one unit per
    /// <see cref="BytesPerWeightUnit"/>, clamped to <see cref="MaxWeight"/>.
    /// </summary>
    /// <remarks>
    /// Payload size is the best pre-execution proxy for how much work a simulation will do: state overrides dominate heavy
    /// simulations, large calldata counts as well, and the size is known before anything is deserialized. The proxy is
    /// wrong in both directions, so it decides queue position only, never admission.
    /// </remarks>
    internal static int Weigh(int paramsUtf8Length) =>
        Math.Clamp(1 + paramsUtf8Length / BytesPerWeightUnit, MinWeight, MaxWeight);

    /// <summary>Acquires one execution permit, waiting at most the configured budget.</summary>
    /// <param name="paramsUtf8Length">Raw <c>params</c> byte length, weighed with <see cref="Weigh"/>; decides queue position only.</param>
    /// <param name="cancellationToken">The caller's lifetime. A caller that gives up stops waiting rather than being granted a permit later.</param>
    /// <param name="allowQueue">Whether a saturated gate may make this request wait; <c>false</c> sheds it at once.</param>
    /// <param name="isTrusted">Whether the caller is authenticated, which queues it at the head regardless of <paramref name="allowQueue"/>.</param>
    /// <returns>A lease that must be disposed exactly once, after the invocation and any task it returned have completed.</returns>
    /// <exception cref="LimitExceededException">No permit was free and none became free within the budget, or the gate is closed.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> fired while waiting.</exception>
    internal ValueTask<Lease> AdmitAsync(int paramsUtf8Length, CancellationToken cancellationToken, bool allowQueue = true, bool isTrusted = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter? waiter = null;
        bool closed;
        bool queueFull = false;
        lock (_lock)
        {
            closed = _closed;
            if (!closed && TryTakeFreePermit())
            {
                return ValueTask.FromResult(new Lease(this));
            }

            // A trusted caller queues even where an anonymous one would be shed; refusing it outright while anonymous
            // callers behind it were still served after a wait is the one outcome a saturated gate must not produce.
            bool mayQueue = (allowQueue || isTrusted) && _budget > TimeSpan.Zero;
            if (!closed && mayQueue && Volatile.Read(ref _liveWaiters) < _maxWaiters)
            {
                // Only the enqueue path pays for the rebuild; the shed path crosses this lock on every saturated request.
                ReapSettledHead(mayRebuild: true);

                waiter = new Waiter(this);
                // long.MinValue rather than an aged deadline: no anonymous arrival may overtake a trusted one, and the
                // sequence still orders trusted callers among themselves by arrival.
                long deadline = isTrusted ? long.MinValue : _timestamp() + Weigh(paramsUtf8Length) * _quantumTicks;
                _waiters.Enqueue(waiter, (deadline, _sequence++));
                // After the enqueue: an enqueue that threw would otherwise leave a count nothing can ever settle.
                Metrics.RpcAdmissionQueued = Interlocked.Increment(ref _liveWaiters);
            }
            else if (!closed)
            {
                queueFull = mayQueue;
                Metrics.RpcAdmissionQueueFullRejections++;
            }
        }

        // Thrown outside the lock: exception dispatch walks the stack before any finally runs, so throwing inside would
        // hold the gate's lock for that whole first pass on the shed path, the hot one under saturation.
        if (waiter is null)
        {
            throw new LimitExceededException(closed ? ShuttingDownMessage : queueFull ? QueueFullMessage : QueueingDisabledMessage);
        }

        return WaitForAdmissionAsync(waiter, cancellationToken);
    }

    private async ValueTask<Lease> WaitForAdmissionAsync(Waiter waiter, CancellationToken cancellationToken)
    {
        bool granted = false;
        try
        {
            // The expiry settles the same completion source the grant does, so exactly one of them wins and a permit
            // can never be handed to a caller that has already given up; the caller's token is linked in for the same reason.
            using CancellationTokenSource expiry = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : new CancellationTokenSource();
            expiry.CancelAfter(_budget);
            using CancellationTokenRegistration registration =
                expiry.Token.UnsafeRegister(static state => _ = ((Waiter)state!).Abandon(), waiter);

            granted = await waiter.Admission;
            // Re-checked after the grant: the caller may have gone away while the permit was in flight.
            if (granted) cancellationToken.ThrowIfCancellationRequested();
        }
        catch when (granted)
        {
            // The permit was transferred to this caller and no Lease will ever reach them to release it.
            Metrics.RpcAdmissionCancellations++;
            Release();
            throw;
        }
        catch
        {
            // Still queued and unsettled - hand back a permit that a concurrent grant may have moved meanwhile; losing
            // to the expiry callback moved none, and a blind Release there would widen the gate by one.
            if (!waiter.Abandon() && waiter.WasGranted) Release();
            throw;
        }

        if (granted)
        {
            return new Lease(this);
        }

        // Separate the three ways a wait ends: the caller left, the gate closed, or the budget ran out.
        if (cancellationToken.IsCancellationRequested)
        {
            Metrics.RpcAdmissionCancellations++;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (Volatile.Read(ref _closedFlag))
        {
            throw new LimitExceededException(ShuttingDownMessage);
        }

        Metrics.RpcAdmissionWaitTimeoutRejections++;
        throw new LimitExceededException(WaitTimeoutMessage);
    }

    private void Release()
    {
        bool releasedTwice;
        lock (_lock)
        {
            releasedTwice = ReleaseCore();
        }

        // Outside the lock, and Release runs from a Lease dispose, so throwing under it on an exception path would
        // replace the caller's exception while the gate's lock is held.
        if (releasedTwice) ThrowReleasedTwice();
    }

    // Caller holds _lock. Hands the permit to the next live waiter, else returns it to the free pool.
    private bool ReleaseCore()
    {
        while (_waiters.TryDequeue(out Waiter? waiter, out _))
        {
            // A settled waiter has already been answered; its slot is reclaimed here rather than by a sweep.
            if (waiter.TryGrant()) return false;
        }

        if (_freePermits == Permits) return true;

        Metrics.RpcAdmissionInFlight = Permits - ++_freePermits;
        return false;
    }

    /// <summary>Closes the gate: everyone queued is answered at once and later arrivals are shed.</summary>
    /// <remarks>Without this, shutdown waits out the whole budget per waiter and then answers them "too many requests"
    /// rather than "shutting down" - a real stall once an operator raises the budget to seconds.</remarks>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_closed) return;

            _closed = true;
            Volatile.Write(ref _closedFlag, true);
            while (_waiters.TryDequeue(out Waiter? waiter, out _)) waiter.Abandon();
        }
    }

    // Caller holds _lock. Takes a free permit only when nobody queued should go first.
    private bool TryTakeFreePermit()
    {
        ReapSettledHead(mayRebuild: false);

        // Release only returns a permit after draining the heap, and this is the only other place one is taken, so a
        // free permit implies an empty queue; without that a permit could sit idle behind a queue nobody will release.
        Debug.Assert(_freePermits == 0 || _waiters.Count == 0, "a permit is free only while the queue is empty");

        if (_waiters.Count > 0 || _freePermits == 0) return false;

        Metrics.RpcAdmissionInFlight = Permits - --_freePermits;
        return true;
    }

    // Caller holds _lock. Settled entries no longer count against the depth cap, so the rebuild only reclaims memory:
    // reaping the head is enough in steady state, the rebuild covers a long stall where expired entries pile up behind
    // a live one, and it is confined to the enqueue path.
    private void ReapSettledHead(bool mayRebuild)
    {
        while (_waiters.TryPeek(out Waiter? head, out _) && head.IsSettled) _waiters.Dequeue();

        if (!mayRebuild || _waiters.Count <= _maxWaiters * 2) return;

        _rebuildScratch.Clear();
        while (_waiters.TryDequeue(out Waiter? waiter, out (long, long) key))
        {
            if (!waiter.IsSettled) _rebuildScratch.Add((waiter, key));
        }

        foreach ((Waiter waiter, (long, long) key) in _rebuildScratch) _waiters.Enqueue(waiter, key);
        _rebuildScratch.Clear();
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowReleasedTwice() =>
        throw new InvalidOperationException("An EVM execution permit was released twice.");

    private sealed class Waiter(EvmAdmissionGate gate)
    {
        // RunContinuationsAsynchronously is load-bearing: TryGrant runs under the gate's lock, and without it the awaiting
        // continuation - which goes on to execute the request - would resume inline there.
        private readonly TaskCompletionSource<bool> _admission = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<bool> Admission => _admission.Task;
        internal bool IsSettled => _admission.Task.IsCompleted;
        internal bool WasGranted => _admission.Task is { IsCompletedSuccessfully: true, Result: true };

        internal bool TryGrant() => Settle(true);
        internal bool Abandon() => Settle(false);

        // Whichever of grant and abandon wins TrySetResult owns the decrement, so the live count stays exact even though
        // Abandon runs on a timer thread without the lock.
        private bool Settle(bool granted)
        {
            if (!_admission.TrySetResult(granted)) return false;

            Metrics.RpcAdmissionQueued = Interlocked.Decrement(ref gate._liveWaiters);
            return true;
        }
    }

    /// <summary>Holds one admission permit; disposing releases it.</summary>
    /// <remarks>
    /// A permit that is never released cannot be recovered, and one released twice throws rather than widening the gate
    /// for the rest of the process. The default lease holds no permit and disposing it is a no-op.
    /// </remarks>
    internal readonly struct Lease(EvmAdmissionGate? gate) : IDisposable
    {
        public void Dispose() => gate?.Release();
    }
}
