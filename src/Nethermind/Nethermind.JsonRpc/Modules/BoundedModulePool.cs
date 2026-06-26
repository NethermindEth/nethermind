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

    public class BoundedModulePool<T> : IRpcModulePool<T> where T : IRpcModule
    {
        private readonly int _timeout;
        private readonly T _shared;
        private readonly Task<T> _sharedAsTask;
        private readonly ConcurrentQueue<T> _pool = new();
        private readonly SemaphoreSlim _semaphore;

        public BoundedModulePool(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout)
        {
            _timeout = timeout;
            Factory = factory;

            _semaphore = new SemaphoreSlim(exclusiveCapacity);
            for (int i = 0; i < exclusiveCapacity; i++)
            {
                _pool.Enqueue(Factory.Create());
            }

            _shared = factory.Create();
            _sharedAsTask = Task.FromResult(_shared);
        }

        public Task<T> GetModule(bool canBeShared) => canBeShared ? SharedPath() : SlowPath();

        private Task<T> SharedPath()
        {
            RpcLimits.AcquireSharedSlot();
            return _sharedAsTask;
        }

        private async Task<T> SlowPath()
        {
            RpcLimits.AcquireQueuedSlot();

            if (!await _semaphore.WaitAsync(_timeout))
            {
                RpcLimits.DecrementQueuedCalls();
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

            RpcLimits.DecrementQueuedCalls();
            _pool.TryDequeue(out T? result);
            return result!;
        }

        public void ReturnModule(T module)
        {
            if (ReferenceEquals(module, _shared))
            {
                RpcLimits.DecrementSharedCalls();
                return;
            }

            _pool.Enqueue(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; }
    }
}
