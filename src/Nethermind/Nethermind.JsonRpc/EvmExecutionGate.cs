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
/// Bounds how many JSON-RPC requests flagged <see cref="Modules.JsonRpcMethodAttribute.IsEvmExecution"/> run at
/// once, lets a short burst wait, and sheds the rest.
/// </summary>
/// <remarks>
/// The flagged set is the <c>eth_call</c> / <c>eth_estimateGas</c> / <c>eth_createAccessList</c> /
/// <c>eth_simulateV1</c> / <c>debug_simulateV1</c> family. It is deliberately not every method that can execute the
/// EVM: the <c>trace_*</c> and <c>debug_traceCall*</c> methods run in their own pools and are not covered, so this
/// is a budget for the simulate family rather than a whole-node EVM budget.
/// <para>
/// Waiters are ordered by a deadline of <c>arrival + weight * quantum</c>, earliest first, rather than by arrival
/// alone. Cheap requests therefore overtake expensive ones and the mean response time drops - a small
/// <c>eth_call</c> does not wait behind three large simulations. Because the deadline is anchored to arrival, the
/// set of requests that can overtake a given one is fixed at those arriving within <c>(weight - 1) * quantum</c>
/// of it; once that window passes, every newly arriving cheap request has a later deadline and cannot pass it.
/// </para>
/// <para>
/// That bounds the <em>number</em> of overtakers, not by itself the wait: a large enough overtaking set still takes
/// longer than the budget to drain. <see cref="MaxQueueDepthPerPermit"/> is what makes the wait bound real - with
/// the queue capped, the work that can be admitted ahead of a waiter is capped too. Beyond that depth the gate
/// sheds on arrival, which is also cheaper than admitting a waiter that is certain to time out.
/// </para>
/// <para>
/// Cost is proxied by the raw <c>params</c> byte length, which is the only estimate available before the request is
/// bound. It is wrong in both directions - a small <c>eth_call</c> into a hot loop is expensive, a large state
/// override may execute trivially - so it is deliberately used only to pick an order, never to admit or refuse.
/// </para>
/// </remarks>
internal sealed class EvmExecutionGate
{
    // Never reaches the caller: ReturnErrorResponse answers every LimitExceededException with "Too many requests".
    private const string SaturatedMessage = "All EVM execution slots are busy.";

    /// <summary>Raw <c>params</c> bytes per unit of weight.</summary>
    internal const int BytesPerWeightUnit = 128 * 1024;

    /// <summary>Heaviest weight class, which also bounds how long a request can be overtaken.</summary>
    internal const int MaxWeight = 4;

    /// <summary>Quanta per wait budget; one quantum is the slack a single unit of weight costs a request.</summary>
    internal const int QuantumsPerBudget = 8;

    /// <summary>Queued waiters allowed per permit before the gate sheds on arrival.</summary>
    internal const int MaxQueueDepthPerPermit = 8;

    private readonly Lock _lock = new();

    // Keyed on (deadline, sequence): PriorityQueue is not a stable heap, so without the sequence two waiters that
    // land on the same tick - or a light arrival colliding exactly with an earlier heavier one - would have no
    // defined order between them, and equal-weight FIFO would hold only by accident of clock resolution.
    private readonly PriorityQueue<Waiter, (long Deadline, long Sequence)> _waiters = new();
    private readonly Func<long> _timestamp;
    private readonly TimeSpan _budget;
    private readonly long _quantumTicks;
    private readonly int _maxPermits;
    private readonly int _maxWaiters;
    private long _sequence;
    private int _freePermits;

    /// <param name="config">Supplies the permit count and the wait budget.</param>
    /// <param name="timestamp">Monotonic tick source; overridden in tests so the aging window is deterministic.</param>
    internal EvmExecutionGate(IJsonRpcConfig config, Func<long>? timestamp = null)
    {
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _maxPermits = _freePermits = Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);
        _maxWaiters = _maxPermits * MaxQueueDepthPerPermit;
        _budget = TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
        // At sub-quantum budgets this floors to zero, collapsing the ordering to arrival - the right degradation,
        // since there is no room to reorder inside a budget that short anyway.
        _quantumTicks = (long)(_budget.TotalSeconds * Stopwatch.Frequency) / QuantumsPerBudget;
    }

    /// <summary>Maps a raw <c>params</c> byte length onto a weight class.</summary>
    internal static int Weigh(int paramsByteLength) =>
        paramsByteLength <= 0 ? 1 : Math.Clamp(1 + paramsByteLength / BytesPerWeightUnit, 1, MaxWeight);

    /// <summary>Acquires one execution permit, waiting at most the configured budget.</summary>
    /// <param name="weight">Cost class from <see cref="Weigh"/>; decides queue position only, never admission.</param>
    /// <param name="allowQueue">Whether a saturated gate may make this request wait; <c>false</c> sheds it immediately.</param>
    /// <returns>A lease that must be disposed exactly once, after the invocation and any task it returned have completed.</returns>
    /// <exception cref="LimitExceededException">No permit was free, and none became free within the budget.</exception>
    internal ValueTask<Lease> AcquireAsync(int weight, bool allowQueue)
    {
        Waiter? waiter = null;
        lock (_lock)
        {
            // A free permit is taken directly only when nobody is already queued, so an arrival cannot barge past
            // waiters that the ordering has already placed ahead of it.
            if (_waiters.Count == 0 && _freePermits > 0)
            {
                _freePermits--;
                return ValueTask.FromResult(new Lease(this));
            }

            DropAbandonedHead();

            if (allowQueue && _budget > TimeSpan.Zero && _waiters.Count < _maxWaiters)
            {
                waiter = new Waiter();
                _waiters.Enqueue(waiter, (_timestamp() + weight * _quantumTicks, _sequence++));
            }
        }

        // Thrown outside the lock: exception dispatch walks the stack looking for a handler before any finally
        // runs, so throwing inside would hold the gate's global lock for that whole first pass - on the shed path,
        // which is the hot one under saturation and the only one batch and authenticated callers take.
        if (waiter is null) throw new LimitExceededException(SaturatedMessage);

        return WaitForAdmissionAsync(waiter);
    }

    private async ValueTask<Lease> WaitForAdmissionAsync(Waiter waiter)
    {
        Interlocked.Increment(ref Metrics.EvmExecutionQueueLength);
        try
        {
            // The expiry settles the same completion source the grant does, so exactly one of them wins and the
            // permit can never be handed to a caller that has already given up.
            using CancellationTokenSource expiry = new(_budget);
            using CancellationTokenRegistration registration =
                expiry.Token.UnsafeRegister(static state => ((Waiter)state!).Abandon(), waiter);

            if (!await waiter.Admission) throw new LimitExceededException(SaturatedMessage);
        }
        catch
        {
            // The waiter is already queued, so a failure setting the expiry up would otherwise leave it there
            // unsettled and the next Release would hand its permit to nobody, losing a slot for good.
            waiter.Abandon();
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref Metrics.EvmExecutionQueueLength);
        }

        return new Lease(this);
    }

    private void Release()
    {
        lock (_lock)
        {
            while (_waiters.TryDequeue(out Waiter? waiter, out _))
            {
                // An abandoned waiter has already been answered with LimitExceeded; its queue slot is reclaimed
                // here rather than by a sweep, so the gate needs no timer of its own.
                if (waiter.TryGrant()) return;
            }

            if (_freePermits == _maxPermits) ThrowReleasedTwice();
            _freePermits++;
        }
    }

    /// <remarks>
    /// Only abandoned waiters stay settled in the queue - a granted one is dequeued by <see cref="Release"/> - so
    /// this keeps a saturated queue from retaining entries nobody is waiting on any more, and keeps them from
    /// counting against the depth cap.
    /// </remarks>
    private void DropAbandonedHead()
    {
        while (_waiters.TryPeek(out Waiter? head, out _) && head.IsSettled) _waiters.Dequeue();
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowReleasedTwice() =>
        throw new InvalidOperationException("An EVM execution permit was released twice.");

    private sealed class Waiter
    {
        // RunContinuationsAsynchronously is load-bearing, not a default: TryGrant runs under the gate's lock, and
        // without it the awaiting continuation - which goes on to execute the request - would resume inline there.
        private readonly TaskCompletionSource<bool> _admission = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<bool> Admission => _admission.Task;
        internal bool IsSettled => _admission.Task.IsCompleted;
        internal bool TryGrant() => _admission.TrySetResult(true);
        internal void Abandon() => _admission.TrySetResult(false);
    }

    /// <summary>Holds one execution permit; disposing releases it.</summary>
    /// <remarks>Deliberately not null-tolerant: a <c>default</c> lease is a bug and should fail loudly rather than
    /// silently skip the release and shrink the gate for the rest of the process.</remarks>
    internal readonly struct Lease(EvmExecutionGate gate) : IDisposable
    {
        public void Dispose() => gate.Release();
    }
}
