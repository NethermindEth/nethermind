// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// Admits JSON-RPC invocations per <see cref="RpcMethodCostClass"/>: a fixed number of permits per gated class,
/// a bounded shortest-job-first wait for the rest, and immediate load shedding once the predicted wait exceeds the budget.
/// </summary>
/// <remarks>
/// Throughput of EVM-bound methods plateaus at roughly one execution per hardware thread; admitting more only
/// converts throughput into queueing delay and, past saturation, into request timeouts. This controller keeps
/// concurrency at the plateau and turns the excess into fast "Too many requests" answers instead: a request is
/// rejected up front when <c>queued weight no heavier than it x EWMA(service time per unit) / permits</c> exceeds
/// its class's wait budget (<see cref="IJsonRpcConfig.MaxQueueWaitMs"/> for EVM execution,
/// <see cref="IJsonRpcConfig.TracingMaxQueueWaitMs"/> and <see cref="IJsonRpcConfig.ProofMaxQueueWaitMs"/> otherwise),
/// and otherwise waits asynchronously for a permit for at most that long. A zero budget disables queueing: a request
/// that finds no free permit is rejected on the calling thread without allocating a waiter. Independently of the
/// budget, at most <see cref="IJsonRpcConfig.RequestQueueLimit"/> requests of a class are queued at once: the
/// prediction is zero until the class has served its first request, so without that backstop a saturated gate with a
/// long budget would queue every arrival.
/// <see cref="RpcMethodCostClass.Default"/> methods are never gated — cheap reads must stay uncapped.
/// <para>
/// Waiters are served lightest first, FIFO within a weight: a freed permit goes to the request expected to finish
/// soonest, which maximises the number of requests served per second of execution time and keeps a sub-millisecond
/// <c>eth_call</c> from waiting behind a batch of heavy simulations. The flip side is deliberate: under sustained
/// overload heavy requests are the ones overtaken until their wait budget runs out, so the gate sheds heavy work first.
/// </para>
/// Admitted invocations run inline on whichever thread the permit is granted on (the request thread when one is
/// free, a thread-pool continuation otherwise); the permit count alone bounds how much of a class's work is in flight.
/// </remarks>
public sealed class RpcAdmissionController
{
    private readonly Gate?[] _gates = new Gate?[Enum.GetValues<RpcMethodCostClass>().Length];

    /// <summary>Creates one gate per gated cost class, sized by the concurrency limits and wait budgets resolved from <paramref name="config"/>.</summary>
    public RpcAdmissionController(IJsonRpcConfig config, ILogManager logManager, TimeProvider? timeProvider = null)
    {
        ILogger logger = logManager.GetClassLogger<RpcAdmissionController>();
        TimeProvider provider = timeProvider ?? TimeProvider.System;
        int maxQueued = Math.Max(0, config.RequestQueueLimit);
        _gates[(int)RpcMethodCostClass.EvmExecution] = new Gate(RpcMethodCostClass.EvmExecution, config.GetEvmExecutionConcurrency(), Math.Max(0, config.MaxQueueWaitMs), maxQueued, logger, provider);
        _gates[(int)RpcMethodCostClass.Tracing] = new Gate(RpcMethodCostClass.Tracing, config.GetTracingConcurrency(), config.GetTracingMaxQueueWaitMs(), maxQueued, logger, provider);
        _gates[(int)RpcMethodCostClass.Proof] = new Gate(RpcMethodCostClass.Proof, config.GetProofConcurrency(), config.GetProofMaxQueueWaitMs(), maxQueued, logger, provider);
    }

    /// <summary>
    /// Acquires a permit for <paramref name="method"/>, waiting at most its cost class's queue-wait budget.
    /// </summary>
    /// <param name="method">The resolved method; its cost class selects the gate.</param>
    /// <param name="paramsUtf8Length">Byte length of the raw <c>params</c> element, or zero when the request carries none; drives the request weight.</param>
    /// <returns>A lease that must be disposed when the invocation, including any returned task, has completed.</returns>
    /// <exception cref="LimitExceededException">
    /// The predicted wait exceeds the budget, or no permit became available within it.
    /// </exception>
    internal ValueTask<Lease> AdmitAsync(ResolvedMethodInfo method, int paramsUtf8Length, CancellationToken cancellationToken = default)
    {
        Gate? gate = _gates[(int)method.CostClass];
        return gate is null
            ? ValueTask.FromResult(default(Lease))
            : gate.AdmitAsync(RpcRequestWeight.Estimate(method, paramsUtf8Length), cancellationToken);
    }

    internal int GetPermits(RpcMethodCostClass costClass) => _gates[(int)costClass]?.Permits ?? 0;
    internal int GetMaxQueueWaitMs(RpcMethodCostClass costClass) => _gates[(int)costClass]?.MaxQueueWaitMs ?? 0;
    internal int GetQueued(RpcMethodCostClass costClass) => _gates[(int)costClass]?.Queued ?? 0;
    internal int GetInFlight(RpcMethodCostClass costClass) => _gates[(int)costClass]?.InFlight ?? 0;
    internal double GetServiceTimeMs(RpcMethodCostClass costClass) => _gates[(int)costClass]?.ServiceTimeMs ?? 0;
    internal void SetServiceTimeMs(RpcMethodCostClass costClass, double serviceTimeMs) => _gates[(int)costClass]?.SetServiceTimeMs(serviceTimeMs);

    /// <summary>Holds one admission permit; disposing releases it and folds the observed service time into the class EWMA.</summary>
    /// <remarks>
    /// A permit that is never released cannot be recovered: once a class's in-flight count sticks at its permit count
    /// with nothing queued, the class sheds every request until restart. <see cref="JsonRpcService"/> therefore settles
    /// every lease exactly once — in a <c>finally</c> around the invocation or, for a streamed result whose re-execution
    /// runs while the response is written, through the response's disposal action. A surplus release cannot be told
    /// from a live lease's release while other requests of the class are in flight, so it raises the effective permit
    /// count by one until the class next drains; the release that then finds nothing in flight is ignored and counted
    /// in <see cref="Metrics.RpcAdmissionReleaseAnomalies"/>, which resynchronises the count.
    /// </remarks>
    internal readonly struct Lease(Gate? gate, int weight, long startTimestamp) : IDisposable
    {
        /// <summary>Whether this lease holds a permit; the default lease of an ungated class holds none and disposing it is a no-op.</summary>
        public bool IsGated => gate is not null;

        public void Dispose() => gate?.Release(weight, startTimestamp);

        /// <summary>Releases the permit without a service-time observation, for a request that was admitted but never invoked.</summary>
        public void ReleaseWithoutSampling() => gate?.Release();
    }

    internal sealed class Gate(RpcMethodCostClass costClass, int permits, int maxQueueWaitMs, int maxQueued, ILogger logger, TimeProvider timeProvider)
    {
        // ~10 requests of memory: fast enough to follow a shift in traffic mix, slow enough to ignore one outlier.
        private const double EwmaAlpha = 0.1;

        private readonly RpcMethodCostClass _costClass = costClass;
        private readonly int _maxQueueWaitMs = maxQueueWaitMs;
        // Zero lifts the cap.
        private readonly int _maxQueued = maxQueued;
        private readonly ILogger _logger = logger;
        private readonly TimeProvider _timeProvider = timeProvider;
        // One lock guards the permit count, the wait queue and the EWMA. Admission is one short critical section per
        // gated call against executions lasting tens of milliseconds, so it is effectively uncontended, and serialising
        // grant and timeout through it is what keeps the two from ever settling the same waiter. The gauges are
        // published inside it too: published after it, two releases can land in reverse order and leave a gauge
        // reading in-flight work at rest until the next request of that class, which may never come.
        private readonly Lock _lock = new();
        // One FIFO per weight (indices RpcRequestWeight.MinWeight..MaxWeight); the lowest non-empty one is served first.
        private readonly Waiter?[] _heads = new Waiter?[RpcRequestWeight.MaxWeight + 1];
        private readonly Waiter?[] _tails = new Waiter?[RpcRequestWeight.MaxWeight + 1];
        private readonly int[] _queuedByWeight = new int[RpcRequestWeight.MaxWeight + 1];
        private int _queued;
        private int _inFlight;
        private double _serviceTimeMs;
        private int _releaseAnomalyLogged;

        public int Permits { get; } = Math.Max(1, permits);
        public int MaxQueueWaitMs => _maxQueueWaitMs;
        public int Queued => Volatile.Read(ref _queued);
        public int InFlight => Volatile.Read(ref _inFlight);
        public double ServiceTimeMs => Volatile.Read(ref _serviceTimeMs);

        public ValueTask<Lease> AdmitAsync(int weight, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Waiter? waiter = null;
            bool queueFull;
            double predictedWaitMs;
            lock (_lock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // A freed permit is handed straight to a waiter, so a free permit means nobody is waiting to be overtaken.
                if (_inFlight < Permits)
                {
                    Metrics.RpcAdmissionInFlight[_costClass] = ++_inFlight;
                    return ValueTask.FromResult(new Lease(this, weight, _timeProvider.GetTimestamp()));
                }

                queueFull = _maxQueued > 0 && _queued >= _maxQueued;
                predictedWaitMs = QueuedWeightNoHeavierThan(weight) * _serviceTimeMs / Permits;
                if (!queueFull && _maxQueueWaitMs > 0 && predictedWaitMs <= _maxQueueWaitMs)
                {
                    waiter = new Waiter(this, weight, cancellationToken);
                    // Armed inside the lock, before the waiter is linked: a grant always finds the timer to dispose, a
                    // firing that races the enqueue blocks until the waiter is fully linked, and a failed arm leaves the
                    // gate untouched.
                    waiter.Timer = _timeProvider.CreateTimer(static state => ((Waiter)state!).OnTimeout(), waiter, TimeSpan.FromMilliseconds(_maxQueueWaitMs), Timeout.InfiniteTimeSpan);
                    Enqueue(waiter);
                    Metrics.RpcAdmissionQueued[_costClass] = ++_queued;
                    waiter.CancellationRegistration = cancellationToken.UnsafeRegister(static state => ((Waiter)state!).OnCancellation(), waiter);
                }
            }

            if (waiter is not null)
            {
                return new ValueTask<Lease>(AwaitGrantAsync(waiter));
            }

            Metrics.RpcAdmissionPredictedWaitRejections.AddOrUpdate(_costClass, 1, static (_, count) => count + 1);
            throw new LimitExceededException(queueFull
                ? $"Unable to start new {_costClass} request. All {Permits} execution slots are busy and {_maxQueued} requests are already queued."
                : _maxQueueWaitMs == 0
                    ? $"Unable to start new {_costClass} request. All {Permits} execution slots are busy and queueing is disabled."
                    : $"Unable to start new {_costClass} request. Predicted queue wait {predictedWaitMs:F0} ms exceeds {_maxQueueWaitMs} ms.");
        }

        // Stamped once the admitted request resumes, so the service time excludes the pool hop between grant and resumption.
        private async Task<Lease> AwaitGrantAsync(Waiter waiter)
        {
            await waiter.Task;
            return new Lease(this, waiter.Weight, _timeProvider.GetTimestamp());
        }

        public void Release(int weight, long startTimestamp) =>
            Release(sampled: true, _timeProvider.GetElapsedTime(startTimestamp).TotalMilliseconds / weight);

        public void Release() => Release(sampled: false, 0);

        private void Release(bool sampled, double observedMsPerUnit)
        {
            Waiter? next = null;
            bool surplus = false;
            lock (_lock)
            {
                // A release with nothing in flight is a lease released twice (or the live release that an earlier double
                // release pre-empted); letting it through would raise the effective permit count for the rest of the
                // process, so it is dropped and reported instead.
                if (_inFlight == 0)
                {
                    Metrics.RpcAdmissionReleaseAnomalies.AddOrUpdate(_costClass, 1, static (_, count) => count + 1);
                    surplus = true;
                }
                else
                {
                    if (sampled)
                    {
                        double current = _serviceTimeMs;
                        double updated = current == 0 ? observedMsPerUnit : current + EwmaAlpha * (observedMsPerUnit - current);
                        Volatile.Write(ref _serviceTimeMs, updated);
                        Metrics.RpcAdmissionServiceTimeMs[_costClass] = updated;
                    }

                    next = DequeueLightest();
                    if (next is null)
                    {
                        Metrics.RpcAdmissionInFlight[_costClass] = --_inFlight;
                    }
                    else
                    {
                        // The permit passes straight on, so in-flight is unchanged.
                        Metrics.RpcAdmissionQueued[_costClass] = --_queued;
                    }
                }
            }

            if (surplus)
            {
                LogSurplusRelease();
            }
            else if (next is not null)
            {
                next.DisposeResources();
                // Completes on the pool: the releasing request's thread never runs the next request's continuation.
                next.TrySetResult();
            }
        }

        // Outside the gate lock: a log sink may block, and the lock must stay a few instructions long.
        private void LogSurplusRelease()
        {
            if (Interlocked.Exchange(ref _releaseAnomalyLogged, 1) == 0)
            {
                if (_logger.IsError) _logger.Error($"A {_costClass} JSON-RPC admission permit was released with none in flight; the surplus release was ignored. Further occurrences are logged at debug level.");
            }
            else if (_logger.IsDebug)
            {
                _logger.Debug($"A {_costClass} JSON-RPC admission permit was released with none in flight; the surplus release was ignored.");
            }
        }

        private void OnTimeout(Waiter waiter)
        {
            lock (_lock)
            {
                // Lost the race to a grant: the request owns the permit and proceeds, so there is nothing to give back.
                if (!waiter.IsQueued)
                {
                    return;
                }

                Unlink(waiter);
                Metrics.RpcAdmissionQueued[_costClass] = --_queued;
            }

            Metrics.RpcAdmissionWaitTimeoutRejections.AddOrUpdate(_costClass, 1, static (_, count) => count + 1);
            waiter.DisposeResources();
            waiter.TrySetException(new LimitExceededException(
                $"Unable to start new {_costClass} request. Not granted an execution slot within {_maxQueueWaitMs} ms."));
        }

        private void OnCancellation(Waiter waiter)
        {
            lock (_lock)
            {
                if (!waiter.IsQueued)
                {
                    return;
                }

                Unlink(waiter);
                Metrics.RpcAdmissionQueued[_costClass] = --_queued;
            }

            waiter.DisposeResources();
            waiter.TrySetCanceled(waiter.CancellationToken);
        }

        private int QueuedWeightNoHeavierThan(int weight)
        {
            int queuedWeight = 0;
            for (int w = RpcRequestWeight.MinWeight; w <= weight; w++)
            {
                queuedWeight += _queuedByWeight[w] * w;
            }

            return queuedWeight;
        }

        private void Enqueue(Waiter waiter)
        {
            int weight = waiter.Weight;
            Waiter? tail = _tails[weight];
            waiter.Previous = tail;
            if (tail is null)
            {
                _heads[weight] = waiter;
            }
            else
            {
                tail.Next = waiter;
            }

            _tails[weight] = waiter;
            _queuedByWeight[weight]++;
            waiter.IsQueued = true;
        }

        private Waiter? DequeueLightest()
        {
            for (int w = RpcRequestWeight.MinWeight; w <= RpcRequestWeight.MaxWeight; w++)
            {
                Waiter? head = _heads[w];
                if (head is not null)
                {
                    Unlink(head);
                    return head;
                }
            }

            return null;
        }

        private void Unlink(Waiter waiter)
        {
            int weight = waiter.Weight;
            if (waiter.Previous is null)
            {
                _heads[weight] = waiter.Next;
            }
            else
            {
                waiter.Previous.Next = waiter.Next;
            }

            if (waiter.Next is null)
            {
                _tails[weight] = waiter.Previous;
            }
            else
            {
                waiter.Next.Previous = waiter.Previous;
            }

            waiter.Previous = waiter.Next = null;
            _queuedByWeight[weight]--;
            waiter.IsQueued = false;
        }

        public void SetServiceTimeMs(double serviceTimeMs)
        {
            lock (_lock)
            {
                Volatile.Write(ref _serviceTimeMs, serviceTimeMs);
                Metrics.RpcAdmissionServiceTimeMs[_costClass] = serviceTimeMs;
            }
        }

        /// <summary>
        /// A queued admission: a node in its weight's FIFO whose task is settled exactly once, by a grant, timeout or
        /// cancellation, whichever unlinks it first under the gate lock.
        /// </summary>
        private sealed class Waiter(Gate gate, int weight, CancellationToken cancellationToken) : TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        {
            public int Weight { get; } = weight;
            public CancellationToken CancellationToken { get; } = cancellationToken;
            public Waiter? Previous;
            public Waiter? Next;
            public ITimer? Timer;
            public bool IsQueued;

            public void OnTimeout() => gate.OnTimeout(this);
            public void OnCancellation() => gate.OnCancellation(this);

            public void DisposeResources()
            {
                if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
                {
                    try
                    {
                        Timer?.Dispose();
                    }
                    finally
                    {
                        DisposeCancellationRegistration();
                    }
                }
            }

            private int _resourcesDisposed;
            private int _registrationSet;
            private int _registrationDisposed;

            public CancellationTokenRegistration CancellationRegistration
            {
                get => _registration;
                set
                {
                    _registration = value;
                    Volatile.Write(ref _registrationSet, 1);
                    if (Volatile.Read(ref _resourcesDisposed) != 0)
                    {
                        DisposeCancellationRegistration(value);
                    }
                }
            }

            private CancellationTokenRegistration _registration;

            private void DisposeCancellationRegistration() =>
                DisposeCancellationRegistration(_registration);

            private void DisposeCancellationRegistration(CancellationTokenRegistration registration)
            {
                if (Volatile.Read(ref _registrationSet) != 0 && Interlocked.Exchange(ref _registrationDisposed, 1) == 0)
                {
                    registration.Dispose();
                }
            }
        }
    }
}
