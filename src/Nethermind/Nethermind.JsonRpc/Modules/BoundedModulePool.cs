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
    public sealed class RpcLimits(int queuedLimit = 0, int sharedLimit = 0)
    {
        /// <summary>
        /// The node-wide limits, used by every <see cref="BoundedModulePool{T}"/> that is not given its own instance.
        /// </summary>
        public static RpcLimits Default { get; } = new();

        public static void Init(int queuedLimit, int sharedLimit)
        {
            Default.QueuedLimit = queuedLimit;
            Default.SharedLimit = sharedLimit;
        }

        private int QueuedLimit { get; set; } = queuedLimit;
        private int SharedLimit { get; set; } = sharedLimit;
        private bool QueuedLimitEnabled => QueuedLimit > 0;
        private bool SharedLimitEnabled => SharedLimit > 0;
        private int _queuedCalls;
        private int _sharedCalls;

        public void AcquireQueuedSlot()
        {
            if (!QueuedLimitEnabled) return;
            int after = Interlocked.Increment(ref _queuedCalls);
            if (after > QueuedLimit)
            {
                Interlocked.Decrement(ref _queuedCalls);
                throw new LimitExceededException($"Unable to start new queued requests. Too many queued requests. Queued calls {after - 1}.");
            }
        }

        public void DecrementQueuedCalls()
        {
            if (QueuedLimitEnabled)
                Interlocked.Decrement(ref _queuedCalls);
        }

        public void AcquireSharedSlot()
        {
            if (!SharedLimitEnabled) return;
            int after = Interlocked.Increment(ref _sharedCalls);
            if (after > SharedLimit)
            {
                Interlocked.Decrement(ref _sharedCalls);
                throw new LimitExceededException($"Unable to start new shared requests. Too many in-flight shared calls. In-flight: {after - 1}.");
            }
        }

        public void DecrementSharedCalls()
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
        private readonly RpcLimits _limits;

        public BoundedModulePool(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout)
            : this(factory, exclusiveCapacity, timeout, RpcLimits.Default)
        {
        }

        internal BoundedModulePool(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout, RpcLimits limits)
        {
            _timeout = timeout;
            _limits = limits;
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
            _limits.AcquireSharedSlot();
            return _sharedAsTask;
        }

        private async Task<T> SlowPath()
        {
            if (!_semaphore.Wait(0))
            {
                _limits.AcquireQueuedSlot();
                try
                {
                    if (!await _semaphore.WaitAsync(_timeout))
                    {
                        throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
                    }
                }
                finally
                {
                    _limits.DecrementQueuedCalls();
                }
            }

            _pool.TryDequeue(out T result);
            return result;
        }

        public void ReturnModule(T module)
        {
            if (ReferenceEquals(module, _shared))
            {
                _limits.DecrementSharedCalls();
                return;
            }

            _pool.Enqueue(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; }
    }
}
