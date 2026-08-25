// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Modules;

/// <summary>
/// Admits JSON-RPC invocations per <see cref="RpcMethodCostClass"/>: a fixed number of permits per gated class,
/// a bounded FIFO wait for the rest, and immediate load shedding once the predicted wait exceeds the budget.
/// </summary>
/// <remarks>
/// Throughput of EVM-bound methods plateaus at roughly one execution per hardware thread; admitting more only
/// converts throughput into queueing delay and, past saturation, into request timeouts. This controller keeps
/// concurrency at the plateau and turns the excess into fast "Too many requests" answers instead: a request is
/// rejected up front when <c>queued x EWMA(service time) / permits x weight</c> exceeds
/// <see cref="IJsonRpcConfig.MaxQueueWaitMs"/>, and otherwise waits asynchronously for a permit for at most that
/// long. <see cref="RpcMethodCostClass.Default"/> methods are never gated — cheap reads must stay uncapped.
/// Admitted invocations run inline on whichever thread the permit is granted on (the request thread when one is
/// free, a thread-pool continuation otherwise); the permit count alone bounds how much of a class's work is in flight.
/// Admission precedes parameter binding, so under saturation a malformed request in a gated class is shed rather
/// than answered with invalid params: the server does not spend CPU validating what it will not execute.
/// </remarks>
public sealed class RpcAdmissionController
{
    private readonly Gate?[] _gates = new Gate?[Enum.GetValues<RpcMethodCostClass>().Length];

    /// <summary>Creates one gate per gated cost class, sized by the concurrency limits resolved from <paramref name="config"/>.</summary>
    public RpcAdmissionController(IJsonRpcConfig config)
    {
        int maxQueueWaitMs = Math.Max(0, config.MaxQueueWaitMs);
        _gates[(int)RpcMethodCostClass.EvmExecution] = new Gate(RpcMethodCostClass.EvmExecution, config.GetEvmExecutionConcurrency(), maxQueueWaitMs);
        _gates[(int)RpcMethodCostClass.Tracing] = new Gate(RpcMethodCostClass.Tracing, config.GetTracingConcurrency(), maxQueueWaitMs);
        _gates[(int)RpcMethodCostClass.Proof] = new Gate(RpcMethodCostClass.Proof, config.GetProofConcurrency(), maxQueueWaitMs);
    }

    /// <summary>
    /// Acquires a permit for <paramref name="method"/>, waiting at most <see cref="IJsonRpcConfig.MaxQueueWaitMs"/>.
    /// </summary>
    /// <param name="method">The resolved method; its cost class selects the gate.</param>
    /// <param name="paramsUtf8Length">Byte length of the raw <c>params</c> element, or zero when unknown; drives the request weight.</param>
    /// <returns>A lease that must be disposed when the invocation, including any returned task, has completed.</returns>
    /// <exception cref="LimitExceededException">
    /// The predicted wait exceeds the budget, or no permit became available within it.
    /// </exception>
    internal ValueTask<Lease> AdmitAsync(ResolvedMethodInfo method, int paramsUtf8Length)
    {
        Gate? gate = _gates[(int)method.CostClass];
        return gate is null
            ? ValueTask.FromResult(default(Lease))
            : gate.AdmitAsync(RpcRequestWeight.Estimate(method, paramsUtf8Length));
    }

    internal int GetPermits(RpcMethodCostClass costClass) => _gates[(int)costClass]?.Permits ?? 0;
    internal int GetQueued(RpcMethodCostClass costClass) => _gates[(int)costClass]?.Queued ?? 0;
    internal int GetInFlight(RpcMethodCostClass costClass) => _gates[(int)costClass]?.InFlight ?? 0;
    internal double GetServiceTimeMs(RpcMethodCostClass costClass) => _gates[(int)costClass]?.ServiceTimeMs ?? 0;
    internal void SetServiceTimeMs(RpcMethodCostClass costClass, double serviceTimeMs) => _gates[(int)costClass]?.SetServiceTimeMs(serviceTimeMs);

    /// <summary>Holds one admission permit; disposing releases it and folds the observed service time into the class EWMA.</summary>
    internal readonly struct Lease(Gate? gate, int weight, long startTimestamp) : IDisposable
    {
        /// <summary>Whether this lease holds a permit; the default lease of an ungated class holds none and disposing it is a no-op.</summary>
        public bool IsGated => gate is not null;

        public void Dispose() => gate?.Release(weight, startTimestamp);
    }

    internal sealed class Gate
    {
        // ~10 requests of memory: fast enough to follow a shift in traffic mix, slow enough to ignore one outlier.
        private const double EwmaAlpha = 0.1;

        private readonly RpcMethodCostClass _costClass;
        private readonly int _maxQueueWaitMs;
        // Never disposed: it allocates no kernel handle here, and in-flight requests still release their permits
        // while the node shuts down.
        private readonly SemaphoreSlim _permits;
        private readonly Lock _ewmaLock = new();
        private int _queued;
        private int _inFlight;
        private double _serviceTimeMs;

        public Gate(RpcMethodCostClass costClass, int permits, int maxQueueWaitMs)
        {
            _costClass = costClass;
            Permits = Math.Max(1, permits);
            _maxQueueWaitMs = maxQueueWaitMs;
            _permits = new SemaphoreSlim(Permits);
        }

        public int Permits { get; }
        public int Queued => Volatile.Read(ref _queued);
        public int InFlight => Volatile.Read(ref _inFlight);
        public double ServiceTimeMs => Volatile.Read(ref _serviceTimeMs);

        public ValueTask<Lease> AdmitAsync(int weight)
        {
            double predictedWaitMs = Queued * ServiceTimeMs * weight / Permits;
            if (predictedWaitMs > _maxQueueWaitMs)
            {
                Metrics.RpcAdmissionPredictedWaitRejections.AddOrUpdate(_costClass, 1, static (_, count) => count + 1);
                throw new LimitExceededException(
                    $"Unable to start new {_costClass} request. Predicted queue wait {predictedWaitMs:F0} ms exceeds {_maxQueueWaitMs} ms.");
            }

            Metrics.RpcAdmissionQueued[_costClass] = Interlocked.Increment(ref _queued);
            Task<bool> wait = _permits.WaitAsync(_maxQueueWaitMs);
            return wait.IsCompletedSuccessfully
                ? ValueTask.FromResult(Admitted(wait.Result, weight))
                : new ValueTask<Lease>(AwaitPermitAsync(wait, weight));
        }

        private async Task<Lease> AwaitPermitAsync(Task<bool> wait, int weight) => Admitted(await wait, weight);

        private Lease Admitted(bool acquired, int weight)
        {
            Metrics.RpcAdmissionQueued[_costClass] = Interlocked.Decrement(ref _queued);
            if (!acquired)
            {
                Metrics.RpcAdmissionWaitTimeoutRejections.AddOrUpdate(_costClass, 1, static (_, count) => count + 1);
                throw new LimitExceededException(
                    $"Unable to start new {_costClass} request. No execution slot freed up within {_maxQueueWaitMs} ms.");
            }

            Metrics.RpcAdmissionInFlight[_costClass] = Interlocked.Increment(ref _inFlight);
            return new Lease(this, weight, Stopwatch.GetTimestamp());
        }

        public void Release(int weight, long startTimestamp)
        {
            double observedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            Metrics.RpcAdmissionInFlight[_costClass] = Interlocked.Decrement(ref _inFlight);
            _permits.Release();
            UpdateServiceTime(observedMs / weight);
        }

        private void UpdateServiceTime(double observedMsPerUnit)
        {
            lock (_ewmaLock)
            {
                double current = _serviceTimeMs;
                SetServiceTimeMs(current == 0 ? observedMsPerUnit : current + EwmaAlpha * (observedMsPerUnit - current));
            }
        }

        public void SetServiceTimeMs(double serviceTimeMs)
        {
            Volatile.Write(ref _serviceTimeMs, serviceTimeMs);
            Metrics.RpcAdmissionServiceTimeMs[_costClass] = serviceTimeMs;
        }
    }
}
