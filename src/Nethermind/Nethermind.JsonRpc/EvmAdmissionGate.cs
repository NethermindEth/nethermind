// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;

namespace Nethermind.JsonRpc;

/// <summary>
/// Admits EVM-executing JSON-RPC requests (see <see cref="Modules.RpcModuleProvider.ResolvedMethodInfo.IsEvmExecution"/>):
/// a fixed number of permits, a bounded shortest-job-first wait for the rest, and immediate load shedding once the
/// predicted wait exceeds the budget.
/// </summary>
/// <remarks>
/// Throughput of EVM-bound methods plateaus at roughly one execution per logical processor, and the override-environment
/// pool they execute in rejects instantly at that count; admitting more only converts throughput into queueing delay and,
/// past saturation, into work wasted on requests that are rejected anyway. The gate keeps concurrency at the plateau
/// (<see cref="IJsonRpcConfig.EvmExecutionConcurrency"/>) and turns the excess into fast "Too many requests" answers: a
/// request is rejected up front when <c>queued weight no heavier than it x EWMA(service time per unit) / permits</c>
/// exceeds <see cref="IJsonRpcConfig.MaxQueueWaitMs"/>, and otherwise waits asynchronously for a permit for at most that
/// long. A zero budget disables queueing: a request that finds no free permit is rejected on the calling thread without
/// allocating a waiter. Independently of the budget, at most <see cref="IJsonRpcConfig.RequestQueueLimit"/> requests wait
/// at once: the prediction is zero until the first request has been served, so without that backstop a saturated gate
/// would queue every arrival.
/// <para>
/// Waiters are served lightest first, FIFO within a weight: a freed permit goes to the request expected to finish soonest,
/// which maximises the requests served per second of execution time and keeps a sub-millisecond <c>eth_call</c> from
/// waiting behind a batch of heavy simulations. The flip side is deliberate: under sustained overload heavy requests are
/// the ones overtaken until their budget runs out, so the gate sheds heavy work first.
/// </para>
/// <para>
/// One lock guards the permit count, the queues and the EWMA; it is held for a few instructions per admission and nothing
/// under it calls out. Expiry is driven by one timer per gate, re-armed to the earliest remaining deadline: with a single
/// constant budget, deadlines are monotonic within a bucket, so expired waiters are always bucket heads. Cancellation is
/// lazy: a waiter whose caller has gone is skipped when a grant reaches it or dropped by the next expiry sweep, at most one
/// budget later; until then it occupies one queue slot and inflates the predicted wait of equal-or-heavier arrivals, but it
/// never receives a permit. The timer is never disposed; the gate lives as long as its <see cref="JsonRpcService"/>.
/// </para>
/// </remarks>
internal sealed class EvmAdmissionGate
{
    internal const int MinWeight = 1;
    internal const int MaxWeight = 8;
    // Hex-encoded JSON is roughly twice the size of the override bytes it carries.
    internal const int BytesPerWeightUnit = 128 * 1024;
    // ~10 requests of memory: fast enough to follow a shift in traffic mix, slow enough to ignore one outlier.
    private const double EwmaAlpha = 0.1;

    // The text never leaves the process: ReturnErrorResponse answers every LimitExceededException with "Too many requests".
    private const string QueueFullMessage = "All EVM execution slots are busy and the request queue is full.";
    private const string QueueingDisabledMessage = "All EVM execution slots are busy and queueing is disabled.";
    private const string PredictedWaitMessage = "All EVM execution slots are busy and the predicted queue wait exceeds the budget.";
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
    private double _serviceTimeMs;
    private int _surplusReleaseLogged;

    /// <summary>Creates a gate sized from <paramref name="config"/>; see <see cref="IJsonRpcConfig.EvmExecutionConcurrency"/> for the permit count rules.</summary>
    internal EvmAdmissionGate(IJsonRpcConfig config, ILogManager logManager, TimeProvider? timeProvider = null)
    {
        _logger = logManager.GetClassLogger<EvmAdmissionGate>();
        _timeProvider = timeProvider ?? TimeProvider.System;

        int envCap = Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);
        Permits = config.EvmExecutionConcurrency is int configured ? Math.Clamp(configured, 1, envCap) : envCap;
        if (config.EvmExecutionConcurrency is int outOfRange && outOfRange != Permits && _logger.IsWarn)
        {
            _logger.Warn($"JsonRpc.EvmExecutionConcurrency={outOfRange} is outside [1, {envCap}]; using {Permits}. Set JsonRpc.MaxQueueWaitMs=0 to disable queueing instead.");
        }

        _budget = TimeSpan.FromMilliseconds(Math.Max(0, config.MaxQueueWaitMs));
        _maxQueued = Math.Max(0, config.RequestQueueLimit);
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
    internal double ServiceTimeMs => Volatile.Read(ref _serviceTimeMs);

    /// <summary>
    /// Converts the byte length of a request's raw <c>params</c> into its admission weight: one unit per
    /// <see cref="BytesPerWeightUnit"/>, clamped to <see cref="MaxWeight"/>.
    /// </summary>
    /// <remarks>
    /// Payload size is the best pre-execution proxy for how much work a simulation will do: state overrides (injected code
    /// plus storage slots) dominate heavy simulations and large calldata counts as well, and the size is known before
    /// anything is deserialized, so a request can be weighed, and shed, without paying for parameter binding. The clamp keeps
    /// a single pathological request from starving everybody else.
    /// </remarks>
    internal static int Weigh(int paramsUtf8Length)
    {
        int weight = MinWeight + paramsUtf8Length / BytesPerWeightUnit;
        return weight > MaxWeight ? MaxWeight : weight;
    }

    /// <summary>Acquires a permit for a request of the given weight, waiting at most <see cref="IJsonRpcConfig.MaxQueueWaitMs"/>.</summary>
    /// <param name="weight">The request weight from <see cref="Weigh"/>; heavier requests wait behind lighter ones.</param>
    /// <param name="cancellationToken">The request's token; a waiter whose token is cancelled never receives a permit.</param>
    /// <returns>A lease that must be released exactly once, after the invocation, including any task it returned, has completed.</returns>
    /// <exception cref="LimitExceededException">
    /// The predicted wait exceeds the budget, queueing is disabled or the queue is full, or no permit was granted within the budget.
    /// </exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before a permit was granted.</exception>
    internal ValueTask<Lease> AdmitAsync(int weight, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool queueFull;
        lock (_lock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_inFlight < Permits)
            {
                Metrics.RpcAdmissionInFlight = ++_inFlight;
                return ValueTask.FromResult(new Lease(this, weight, _timeProvider.GetTimestamp()));
            }

            queueFull = _maxQueued > 0 && _queued >= _maxQueued;
            double predictedWaitMs = QueuedWeightNoHeavierThan(weight) * _serviceTimeMs / Permits;
            if (!queueFull && _budget > TimeSpan.Zero && predictedWaitMs <= _budget.TotalMilliseconds)
            {
                // Stamped under the lock: with one constant budget every new deadline is then no earlier than any queued one,
                // so expired waiters are always bucket heads and a timer armed only when the queue was empty is never due
                // later than the earliest deadline rounded up to a whole millisecond.
                long now = _timeProvider.GetTimestamp();
                Waiter waiter = new(weight, now, cancellationToken);
                bool wasEmpty = _queued == 0;
                _queues[weight].Enqueue(waiter);
                Metrics.RpcAdmissionQueued = ++_queued;
                if (wasEmpty)
                {
                    ArmSweep(now);
                }

                return new ValueTask<Lease>(waiter.Task);
            }

            Metrics.RpcAdmissionPredictedWaitRejections++;
        }

        throw new LimitExceededException(queueFull
            ? QueueFullMessage
            : _budget <= TimeSpan.Zero ? QueueingDisabledMessage : PredictedWaitMessage);
    }

    internal void SetServiceTimeMs(double serviceTimeMs)
    {
        lock (_lock)
        {
            Volatile.Write(ref _serviceTimeMs, serviceTimeMs);
            Metrics.RpcAdmissionServiceTimeMs = serviceTimeMs;
        }
    }

    private void Release(int weight, long startTimestamp) =>
        Release(sampled: true, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds / weight);

    private void Release() => Release(sampled: false, 0);

    private void Release(bool sampled, double observedMsPerUnit)
    {
        Waiter? grantee = null;
        Waiter? cancelled = null;
        bool surplus = false;
        long grantTimestamp = 0;
        lock (_lock)
        {
            // A release with nothing in flight is a lease released twice; letting it through would raise the permit count
            // for the rest of the process.
            if (_inFlight == 0)
            {
                surplus = true;
            }
            else
            {
                if (sampled)
                {
                    double updated = _serviceTimeMs == 0 ? observedMsPerUnit : _serviceTimeMs + EwmaAlpha * (observedMsPerUnit - _serviceTimeMs);
                    Volatile.Write(ref _serviceTimeMs, updated);
                    Metrics.RpcAdmissionServiceTimeMs = updated;
                }

                for (int w = MinWeight; w <= MaxWeight && grantee is null; w++)
                {
                    while (_queues[w].TryDequeue(out Waiter? head))
                    {
                        Metrics.RpcAdmissionQueued = --_queued;
                        if (!head.CancellationToken.IsCancellationRequested)
                        {
                            grantee = head;
                            break;
                        }

                        // A caller that has gone never takes the permit.
                        Metrics.RpcAdmissionCancellations++;
                        head.NextSettled = cancelled;
                        cancelled = head;
                    }
                }

                if (grantee is null)
                {
                    Metrics.RpcAdmissionInFlight = --_inFlight;
                }
                else
                {
                    // The permit passes straight on, so in-flight is unchanged. Stamped at grant rather than when the grantee resumes:
                    // the wait for a pool thread is latency the caller pays, so including it makes the gate shed sooner when the pool
                    // itself is the bottleneck.
                    grantTimestamp = _timeProvider.GetTimestamp();
                }
            }
        }

        if (surplus)
        {
            LogSurplusRelease();
        }

        for (Waiter? waiter = cancelled; waiter is not null; waiter = waiter.NextSettled)
        {
            waiter.TrySetCanceled(waiter.CancellationToken);
        }

        grantee?.TrySetResult(new Lease(this, grantee.Weight, grantTimestamp));
    }

    // Outside the gate lock: a log sink may block.
    private void LogSurplusRelease()
    {
        if (Interlocked.Exchange(ref _surplusReleaseLogged, 1) == 0)
        {
            if (_logger.IsError) _logger.Error("An EVM admission permit was released with none in flight; the surplus release was ignored. Further occurrences are logged at debug level.");
        }
        else if (_logger.IsDebug)
        {
            _logger.Debug("An EVM admission permit was released with none in flight; the surplus release was ignored.");
        }
    }

    // Timer callback: every step is under the lock and idempotent, so an overlapping fire is harmless, and nothing here throws.
    private void Sweep()
    {
        Waiter? expired = null;
        Waiter? cancelled = null;
        lock (_lock)
        {
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

        for (Waiter? waiter = cancelled; waiter is not null; waiter = waiter.NextSettled)
        {
            waiter.TrySetCanceled(waiter.CancellationToken);
        }

        for (Waiter? waiter = expired; waiter is not null; waiter = waiter.NextSettled)
        {
            waiter.TrySetException(new LimitExceededException(WaitTimeoutMessage));
        }
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

        // Change truncates to whole milliseconds: a fractional remainder would fire early, find the head unexpired and
        // re-fire at once until the clock passes the deadline, so it is rounded up (a waiter is shed at most a millisecond
        // late). A negative due time throws inside the timer callback, and between -2 ms and -1 ms it truncates to
        // Infinite and silently disarms.
        TimeSpan due = _budget - _timeProvider.GetElapsedTime(earliest, now);
        due = due <= TimeSpan.Zero ? TimeSpan.Zero : TimeSpan.FromMilliseconds(Math.Ceiling(due.TotalMilliseconds));

        _sweepTimer.Change(due, Timeout.InfiniteTimeSpan);
    }

    private int QueuedWeightNoHeavierThan(int weight)
    {
        int queuedWeight = 0;
        for (int w = MinWeight; w <= weight; w++)
        {
            queuedWeight += _queues[w].Count * w;
        }

        return queuedWeight;
    }

    /// <summary>Holds one admission permit; disposing releases it and folds the observed service time into the EWMA.</summary>
    /// <remarks>
    /// A permit that is never released cannot be recovered: once the in-flight count sticks at the permit count with nothing
    /// queued, the gate sheds every request until restart, so <see cref="JsonRpcService"/> settles every lease exactly once
    /// in a <c>finally</c>. A surplus release cannot be told from a live lease's while other requests are in flight, so it
    /// raises the effective permit count by one until the gate next drains; the release that then finds nothing in flight is
    /// dropped and logged, which resynchronises the count. The default lease holds no permit and releasing it is a no-op.
    /// </remarks>
    internal readonly struct Lease(EvmAdmissionGate? gate, int weight, long startTimestamp) : IDisposable
    {
        public void Dispose() => gate?.Release(weight, startTimestamp);

        /// <summary>Releases the permit without a service-time observation, for a request that was admitted but never invoked.</summary>
        public void ReleaseWithoutSampling() => gate?.Release();
    }

    /// <summary>
    /// A queued admission, dequeued exactly once under the gate lock by either a grant or the expiry sweep, and settled by
    /// that dequeuer outside the lock through the <see cref="NextSettled"/> chain.
    /// </summary>
    private sealed class Waiter(int weight, long enqueuedTimestamp, CancellationToken cancellationToken)
        // Completed on the pool: the releasing request's thread never runs the next request's invocation.
        : TaskCompletionSource<Lease>(TaskCreationOptions.RunContinuationsAsynchronously)
    {
        public int Weight { get; } = weight;
        public long EnqueuedTimestamp { get; } = enqueuedTimestamp;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public Waiter? NextSettled;
    }
}
