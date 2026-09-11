// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

/// <summary>
/// Admits EVM-executing JSON-RPC requests (those flagged <see cref="Modules.JsonRpcMethodAttribute.IsEvmExecution"/>):
/// a fixed number of permits, a bounded shortest-job-first wait for the rest, and load shedding beyond that.
/// </summary>
/// <remarks>
/// Throughput of EVM-bound methods plateaus at roughly one execution per logical processor, and the override-environment
/// pool they execute in rejects instantly at that count; admitting more only converts throughput into queueing delay and,
/// past saturation, into work wasted on requests that are rejected anyway. The gate keeps concurrency at the plateau
/// (<see cref="IJsonRpcConfig.EvmExecutionConcurrency"/>) and turns the excess into fast "Too many requests" answers: a
/// request that finds no free permit waits asynchronously for at most <see cref="IJsonRpcConfig.EvmExecutionMaxQueueWaitMs"/>,
/// and is rejected up front when <see cref="IJsonRpcConfig.EvmExecutionQueueLimit"/> requests are already waiting. A zero
/// budget disables queueing: the request is rejected on the calling thread without allocating a waiter. A negative queue
/// limit also disables queueing; zero explicitly removes the queue limit.
/// <para>
/// Waiters are served lightest first, FIFO within a weight: a freed permit goes to the request expected to finish soonest,
/// which maximises the requests served per second of execution time and keeps a sub-millisecond <c>eth_call</c> from
/// waiting behind a batch of heavy simulations. The flip side is deliberate: under sustained overload heavy requests are
/// the ones overtaken until their budget runs out, so the gate sheds heavy work first. "Heavy" means large, not expensive:
/// the weight is the <c>params</c> size (see <see cref="Weigh"/>), so a large but cheap request, say many storage overrides
/// ahead of a trivial call, is overtaken at every release and, for as long as the overload lasts, shed at its budget even
/// though it would have finished quickly. The gate bounds every caller's wait; it does not promise that a large request is
/// eventually served while the node stays overloaded.
/// </para>
/// <para>
/// One lock guards the permit count and the queues; it is held for a few instructions per admission and nothing under it
/// calls out. Expiry is driven by one timer per gate, re-armed to the earliest remaining deadline: with a single constant
/// budget, deadlines are monotonic within a bucket, so expired waiters are always bucket heads. Cancellation is lazy: a
/// waiter whose caller has gone is skipped when a grant reaches it or dropped by the next expiry sweep; until then it occupies
/// one queue slot, but it never receives a permit. Disposing the gate settles queued admissions with cancellation or
/// disposal failure and rejects new ones.
/// </para>
/// </remarks>
internal sealed class EvmAdmissionGate : IDisposable
{
    internal const int MinWeight = 1;
    internal const int MaxWeight = 8;
    // Hex-encoded JSON is roughly twice the size of the override bytes it carries.
    internal const int BytesPerWeightUnit = 128 * 1024;

    // The text never leaves the process: ReturnErrorResponse answers every LimitExceededException with "Too many requests".
    private const string QueueFullMessage = "All EVM execution slots are busy and the request queue is full.";
    private const string QueueingDisabledMessage = "All EVM execution slots are busy and queueing is disabled.";
    private const string WaitTimeoutMessage = "Not granted an EVM execution slot within the queue wait budget.";

    private readonly Lock _lock = new();
    private readonly Queue<Waiter>[] _queues = new Queue<Waiter>[MaxWeight + 1];
    private readonly TimeSpan _budget;
    private readonly int _maxQueued;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ITimer _sweepTimer;
    private int _queued;
    private int _inFlight;
    private bool _disposed;

    /// <summary>Creates a gate sized from <paramref name="config"/>; see <see cref="IJsonRpcConfig.EvmExecutionConcurrency"/> for the permit count rules.</summary>
    internal EvmAdmissionGate(IJsonRpcConfig config, ILogManager logManager, TimeProvider? timeProvider = null)
    {
        _logger = logManager.GetClassLogger<EvmAdmissionGate>();
        _timeProvider = timeProvider ?? TimeProvider.System;

        int envCap = Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);
        Permits = config.EvmExecutionConcurrency is int configured ? Math.Clamp(configured, 1, envCap) : envCap;
        if (config.EvmExecutionConcurrency is int outOfRange && outOfRange != Permits && _logger.IsWarn)
        {
            _logger.Warn($"JsonRpc.EvmExecutionConcurrency={outOfRange} is outside [1, {envCap}]; using {Permits}. Set JsonRpc.EvmExecutionMaxQueueWaitMs=0 to disable queueing instead.");
        }

        _budget = config.EvmExecutionQueueLimit < 0
            ? TimeSpan.Zero
            : TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
        _maxQueued = Math.Max(0, config.EvmExecutionQueueLimit);
        for (int w = MinWeight; w <= MaxWeight; w++)
        {
            _queues[w] = new Queue<Waiter>();
        }

        // Created up front so that arming, which happens after a waiter is already queued and counted, can only Change and never throw;
        // after the queues exist, since a provider may run the callback from inside CreateTimer.
        _sweepTimer = _timeProvider.CreateTimer(static state => ((EvmAdmissionGate)state!).Sweep(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    internal int Permits { get; }
    internal int Queued => Volatile.Read(ref _queued);
    internal int InFlight => Volatile.Read(ref _inFlight);

    public void Dispose()
    {
        Waiter? disposed = null;
        Waiter? cancelled = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _sweepTimer.Dispose();
            for (int w = MinWeight; w <= MaxWeight; w++)
            {
                while (_queues[w].TryDequeue(out Waiter? waiter))
                {
                    Metrics.RpcAdmissionQueued = --_queued;
                    if (waiter.CancellationToken.IsCancellationRequested)
                    {
                        Metrics.RpcAdmissionCancellations++;
                        waiter.NextSettled = cancelled;
                        cancelled = waiter;
                    }
                    else
                    {
                        waiter.NextSettled = disposed;
                        disposed = waiter;
                    }
                }
            }
        }

        CompleteWaiters(cancelled, disposed, isDisposed: true);
    }

    /// <summary>
    /// Converts the byte length of a request's raw <c>params</c> into its admission weight: one unit per
    /// <see cref="BytesPerWeightUnit"/>, clamped to <see cref="MaxWeight"/>.
    /// </summary>
    /// <remarks>
    /// Payload size is the best pre-execution proxy for how much work a simulation will do: state overrides (injected code
    /// plus storage slots) dominate heavy simulations and large calldata counts as well, and the size is known before
    /// anything is deserialized, so a request can be weighed without paying for parameter binding. The clamp keeps a single
    /// pathological request from starving everybody else. The proxy is wrong in both directions, tiny calldata can drive an
    /// expensive contract and many overrides can precede a trivial call, but only the second error compounds: with
    /// shortest-job-first an overweighted request is overtaken at every release (see the class remarks).
    /// </remarks>
    internal static int Weigh(int paramsUtf8Length)
    {
        int weight = MinWeight + paramsUtf8Length / BytesPerWeightUnit;
        return weight > MaxWeight ? MaxWeight : weight;
    }

    /// <summary>
    /// Acquires a permit for a request whose raw <c>params</c> span <paramref name="paramsUtf8Length"/> bytes, waiting at
    /// most <see cref="IJsonRpcConfig.EvmExecutionMaxQueueWaitMs"/>.
    /// </summary>
    /// <param name="paramsUtf8Length">Byte length of the raw <c>params</c>, weighed with <see cref="Weigh"/>; heavier requests wait behind lighter ones.</param>
    /// <param name="cancellationToken">The request's token; a waiter whose token is cancelled never receives a permit.</param>
    /// <param name="allowQueue">Whether a saturated gate may add this request to its wait queue.</param>
    /// <returns>A lease that must be disposed exactly once, after the invocation, including any task it returned, has completed.</returns>
    /// <exception cref="LimitExceededException">
    /// Queueing is disabled or the queue is full, or no permit was granted within the budget.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a permit was granted.</exception>
    internal ValueTask<Lease> AdmitAsync(int paramsUtf8Length, CancellationToken cancellationToken, bool allowQueue = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int weight = Weigh(paramsUtf8Length);
        bool queueFull;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (_inFlight < Permits)
            {
                Metrics.RpcAdmissionInFlight = ++_inFlight;
                return ValueTask.FromResult(new Lease(this));
            }

            queueFull = _maxQueued > 0 && _queued >= _maxQueued;
            if (allowQueue && !queueFull && _budget > TimeSpan.Zero)
            {
                // Stamped under the lock: with one constant budget every new deadline is then no earlier than any queued one,
                // so expired waiters are always bucket heads.
                long now = _timeProvider.GetTimestamp();
                Waiter waiter = new(now, cancellationToken);
                bool wasEmpty = _queued == 0;
                _queues[weight].Enqueue(waiter);
                Metrics.RpcAdmissionQueued = ++_queued;
                if (wasEmpty)
                {
                    ArmSweep(now);
                }

                return new ValueTask<Lease>(waiter.Task);
            }

            Metrics.RpcAdmissionQueueFullRejections++;
        }

        throw new LimitExceededException(queueFull ? QueueFullMessage : QueueingDisabledMessage);
    }

    private void Release()
    {
        Waiter? grantee = null;
        Waiter? cancelled = null;
        Waiter? expired = null;
        lock (_lock)
        {
            Debug.Assert(_inFlight > 0, "a lease was released twice");
            long now = _queued > 0 ? _timeProvider.GetTimestamp() : 0;
            for (int w = MinWeight; w <= MaxWeight && grantee is null; w++)
            {
                while (_queues[w].TryDequeue(out Waiter? head))
                {
                    Metrics.RpcAdmissionQueued = --_queued;
                    bool isCancelled = head.CancellationToken.IsCancellationRequested;
                    if (!isCancelled && _timeProvider.GetElapsedTime(head.EnqueuedTimestamp, now) < _budget)
                    {
                        grantee = head;
                        break;
                    }

                    if (isCancelled)
                    {
                        // A caller that has gone never takes the permit.
                        Metrics.RpcAdmissionCancellations++;
                        head.NextSettled = cancelled;
                        cancelled = head;
                    }
                    else
                    {
                        Metrics.RpcAdmissionWaitTimeoutRejections++;
                        head.NextSettled = expired;
                        expired = head;
                    }
                }
            }

            // The permit passes straight on to the grantee, so in-flight changes only when nobody is waiting.
            if (grantee is null)
            {
                Metrics.RpcAdmissionInFlight = --_inFlight;
            }
        }

        CompleteWaiters(cancelled, expired);
        grantee?.TrySetResult(new Lease(this));
    }

    // Timer callback: every step is under the lock and idempotent, so an overlapping fire is harmless.
    private void Sweep()
    {
        Waiter? expired = null;
        Waiter? cancelled = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            long now = _timeProvider.GetTimestamp();
            for (int w = MinWeight; w <= MaxWeight; w++)
            {
                Queue<Waiter> queue = _queues[w];
                while (queue.TryPeek(out Waiter? head))
                {
                    bool isCancelled = head.CancellationToken.IsCancellationRequested;
                    // Expires at exactly the budget: the timer is armed for that instant, rounded up to a whole millisecond.
                    if (!isCancelled && _timeProvider.GetElapsedTime(head.EnqueuedTimestamp, now) < _budget)
                    {
                        break;
                    }

                    queue.Dequeue();
                    Metrics.RpcAdmissionQueued = --_queued;
                    if (isCancelled)
                    {
                        Metrics.RpcAdmissionCancellations++;
                        head.NextSettled = cancelled;
                        cancelled = head;
                    }
                    else
                    {
                        Metrics.RpcAdmissionWaitTimeoutRejections++;
                        head.NextSettled = expired;
                        expired = head;
                    }
                }
            }

            ArmSweep(now);
        }

        CompleteWaiters(cancelled, expired);
    }

    // Caller holds _lock.
    private void ArmSweep(long now)
    {
        bool anyQueued = false;
        long earliest = 0;
        for (int w = MinWeight; w <= MaxWeight; w++)
        {
            if (_queues[w].TryPeek(out Waiter? head) && (!anyQueued || head.EnqueuedTimestamp < earliest))
            {
                earliest = head.EnqueuedTimestamp;
                anyQueued = true;
            }
        }

        // Never disarmed: a fire that finds nothing queued is inert, so an empty queue needs no timer state of its own.
        if (!anyQueued)
        {
            return;
        }

        // Timer due times have millisecond resolution; round up to avoid firing before a deadline. Timer scheduling may
        // still run the sweep later, so the elapsed-time check above remains authoritative.
        TimeSpan due = _budget - _timeProvider.GetElapsedTime(earliest, now);
        due = due <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Ceiling(due.TotalMilliseconds));

        _sweepTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private static void CompleteWaiters(Waiter? cancelled, Waiter? rejected, bool isDisposed = false)
    {
        for (Waiter? waiter = cancelled; waiter is not null; waiter = waiter.NextSettled)
        {
            waiter.TrySetCanceled(waiter.CancellationToken);
        }

        for (Waiter? waiter = rejected; waiter is not null; waiter = waiter.NextSettled)
        {
            waiter.TrySetException(isDisposed
                ? new ObjectDisposedException(nameof(EvmAdmissionGate))
                : new LimitExceededException(WaitTimeoutMessage));
        }
    }

    /// <summary>Holds one admission permit; disposing releases it.</summary>
    /// <remarks>
    /// A permit that is never released cannot be recovered: once the in-flight count sticks at the permit count with nothing
    /// queued, the gate sheds every request until restart. A permit released twice raises the effective permit count for
    /// the rest of the process. <see cref="JsonRpcService"/> therefore holds every lease in a <c>using</c>. The default
    /// lease holds no permit and disposing it is a no-op.
    /// </remarks>
    internal readonly struct Lease(EvmAdmissionGate? gate) : IDisposable
    {
        public void Dispose() => gate?.Release();
    }

    /// <summary>
    /// A queued admission, dequeued exactly once under the gate lock by either a grant or the expiry sweep, and settled by
    /// that dequeuer outside the lock through the <see cref="NextSettled"/> chain.
    /// </summary>
    private sealed class Waiter(long enqueuedTimestamp, CancellationToken cancellationToken)
        // Completed on the pool: the releasing request's thread never runs the next request's invocation.
        : TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Waiter? NextSettled;
    }
}
