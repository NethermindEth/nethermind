// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc;

/// <summary>
/// Bounds how many EVM-executing JSON-RPC requests (those flagged
/// <see cref="Modules.JsonRpcMethodAttribute.IsEvmExecution"/>) run at once, lets a short burst wait, and sheds the rest.
/// </summary>
/// <remarks>
/// Throughput of EVM-bound methods plateaus at roughly one execution per logical processor, so admitting more only
/// converts throughput into queueing delay and starves block processing. One aggregate budget covers every flagged
/// method, which is stricter than the per-environment-pool caps they execute in; those pools stay as a backstop for
/// callers that do not come through JSON-RPC.
/// <para>
/// Admission is strictly FIFO: <see cref="SemaphoreSlim"/> hands a released permit to the longest-waiting caller, so no
/// request can be overtaken and every wait is bounded by <see cref="IJsonRpcConfig.EvmExecutionMaxQueueWaitMs"/>.
/// Ordering by predicted cost was considered and rejected: without aging it starves the large state-override
/// simulations this gate exists to protect, and the only available cost proxy is the raw parameter size.
/// </para>
/// <para>
/// The semaphore is constructed with a maximum count, so releasing a lease twice throws <see cref="SemaphoreFullException"/>
/// rather than silently raising the effective concurrency for the rest of the process.
/// </para>
/// <para>
/// Deliberately not disposable: no call path takes a blocking synchronous wait, so the semaphore never allocates its
/// wait handle and holds nothing to release. Disposing it at shutdown would instead risk leaving an already-granted
/// waiter's task permanently incomplete.
/// </para>
/// </remarks>
internal sealed class EvmExecutionGate
{
    // Never reaches the caller: ReturnErrorResponse answers every LimitExceededException with "Too many requests".
    private const string SaturatedMessage = "All EVM execution slots are busy.";

    private readonly SemaphoreSlim _permits;
    private readonly TimeSpan _budget;

    internal EvmExecutionGate(IJsonRpcConfig config)
    {
        int permits = Math.Max(1, config.EthModuleConcurrentInstances ?? Environment.ProcessorCount);
        _permits = new SemaphoreSlim(permits, permits);
        _budget = TimeSpan.FromMilliseconds(Math.Max(0, config.EvmExecutionMaxQueueWaitMs));
    }

    /// <summary>Acquires one execution permit, waiting at most the configured budget.</summary>
    /// <param name="allowQueue">Whether a saturated gate may make this request wait; <c>false</c> sheds it immediately.</param>
    /// <returns>A lease that must be disposed exactly once, after the invocation and any task it returned have completed.</returns>
    /// <exception cref="LimitExceededException">No permit was free, and none became free within the budget.</exception>
    internal ValueTask<Lease> AcquireAsync(bool allowQueue)
    {
        // Cannot barge past the queue: a Release with waiters pending hands the count straight to the longest-waiting
        // caller rather than back to the semaphore, so this only succeeds when nothing is waiting.
        if (_permits.Wait(0)) return ValueTask.FromResult(new Lease(this));
        if (!allowQueue || _budget <= TimeSpan.Zero) throw new LimitExceededException(SaturatedMessage);

        return QueueAsync();
    }

    private async ValueTask<Lease> QueueAsync()
    {
        Interlocked.Increment(ref Metrics.EvmExecutionQueueLength);
        try
        {
            if (!await _permits.WaitAsync(_budget)) throw new LimitExceededException(SaturatedMessage);
        }
        finally
        {
            Interlocked.Decrement(ref Metrics.EvmExecutionQueueLength);
        }

        return new Lease(this);
    }

    /// <summary>Holds one execution permit; disposing releases it.</summary>
    internal readonly struct Lease(EvmExecutionGate? gate) : IDisposable
    {
        public void Dispose() => gate?._permits.Release();
    }
}
