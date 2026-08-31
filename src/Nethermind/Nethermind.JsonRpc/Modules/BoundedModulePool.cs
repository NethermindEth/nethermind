// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc.Modules
{
    // Two independent counters:
    //   _queuedCalls: SlowPath waiters, bounded by RequestQueueLimit.
    //   _sharedCalls: SharedPath in-flight, bounded by MaxConcurrentSharedRequests — caps memory
    //                 for heavy sharable methods (eth_call / eth_estimateGas / eth_createAccessList).
    public static class RpcLimits
    {
        public static void Init(int queuedLimit, int sharedLimit)
        {
            QueuedLimit = queuedLimit;
            SharedLimit = sharedLimit;
        }

        private static int QueuedLimit { get; set; }
        private static int SharedLimit { get; set; }
        private static bool QueuedLimitEnabled => QueuedLimit > 0;
        private static bool SharedLimitEnabled => SharedLimit > 0;
        private static int _queuedCalls;
        private static int _sharedCalls;

        public static void AcquireQueuedSlot()
        {
            if (!QueuedLimitEnabled) return;
            int after = Interlocked.Increment(ref _queuedCalls);
            if (after > QueuedLimit)
            {
                Interlocked.Decrement(ref _queuedCalls);
                throw new LimitExceededException($"Unable to start new queued requests. Too many queued requests. Queued calls {after - 1}.");
            }
        }

        public static void DecrementQueuedCalls()
        {
            if (QueuedLimitEnabled)
                Interlocked.Decrement(ref _queuedCalls);
        }

        public static void AcquireSharedSlot()
        {
            if (!SharedLimitEnabled) return;
            int after = Interlocked.Increment(ref _sharedCalls);
            if (after > SharedLimit)
            {
                Interlocked.Decrement(ref _sharedCalls);
                throw new LimitExceededException($"Unable to start new shared requests. Too many in-flight shared calls. In-flight: {after - 1}.");
            }
        }

        public static void DecrementSharedCalls()
        {
            if (SharedLimitEnabled)
                Interlocked.Decrement(ref _sharedCalls);
        }
    }

    /// <summary>
    /// Module pool with one shared instance for sharable calls and up to <c>exclusiveCapacity</c> exclusive
    /// instances, all created on first use.
    /// </summary>
    /// <remarks>
    /// Lazy creation keeps the capacity cheap to raise: each instance is a full DI child scope, and a node that never
    /// receives, say, a <c>trace_*</c> call should not pay for a processor's worth of them at startup. The semaphore
    /// bounds concurrent exclusive rentals, and every returned instance goes back to the idle queue, so a rental only
    /// finds the queue empty while fewer than <c>exclusiveCapacity</c> instances exist — the total never exceeds the
    /// capacity. <see cref="Preload"/> restores eager creation for operators who prefer first-request latency; it is
    /// meant to run before the first rental, as a rental racing it can create one instance beyond the capacity.
    /// </remarks>
    public class BoundedModulePool<T>(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout) : IRpcModulePool<T> where T : IRpcModule
    {
        private readonly ConcurrentQueue<T> _pool = new();
        private readonly SemaphoreSlim _semaphore = new(exclusiveCapacity);
        private readonly Lock _sharedLock = new();
        // Factories supplied by plugins historically observed serial Create calls. Keep that compatibility while
        // retaining lazy creation: creations are serialized, so a first burst of N rentals builds N instances one
        // at a time while each caller holds its slot. Set PreloadRpcModules to pay that cost at startup instead.
        private readonly Lock _factoryLock = new();
        // Published under _sharedLock, read lock-free on the rental and return paths.
        private volatile Task<T>? _sharedAsTask;
        private int _createdExclusive;

        public void Preload()
        {
            GetOrCreateShared();
            int created = Volatile.Read(ref _createdExclusive);
            while (created < exclusiveCapacity)
            {
                int witnessed = Interlocked.CompareExchange(ref _createdExclusive, created + 1, created);
                if (witnessed == created)
                {
                    _pool.Enqueue(CreateModule());
                    created++;
                }
                else
                {
                    created = witnessed;
                }
            }
        }

        public Task<T> GetModule(bool canBeShared) => canBeShared ? SharedPath() : SlowPath();

        private Task<T> SharedPath()
        {
            // Created before the slot is taken: a failing factory must not consume a slot that is never returned.
            Task<T> shared = _sharedAsTask ?? GetOrCreateShared();
            RpcLimits.AcquireSharedSlot();
            return shared;
        }

        private Task<T> GetOrCreateShared()
        {
            lock (_sharedLock)
            {
                return _sharedAsTask ??= Task.FromResult(CreateModule());
            }
        }

        private async Task<T> SlowPath()
        {
            RpcLimits.AcquireQueuedSlot();

            if (!await _semaphore.WaitAsync(timeout))
            {
                RpcLimits.DecrementQueuedCalls();
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

            RpcLimits.DecrementQueuedCalls();
            if (_pool.TryDequeue(out T? result))
            {
                return result;
            }

            try
            {
                Interlocked.Increment(ref _createdExclusive);
                return CreateModule();
            }
            catch
            {
                Interlocked.Decrement(ref _createdExclusive);
                _semaphore.Release();
                throw;
            }
        }

        public void ReturnModule(T module)
        {
            // Only ever a completed task, so Result is a plain read.
            Task<T>? shared = _sharedAsTask;
            if (shared is not null && ReferenceEquals(module, shared.Result))
            {
                RpcLimits.DecrementSharedCalls();
                return;
            }

            _pool.Enqueue(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; } = factory;

        private T CreateModule()
        {
            lock (_factoryLock)
            {
                return Factory.Create();
            }
        }
    }
}
