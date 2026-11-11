// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;

namespace Nethermind.JsonRpc.Modules
{
    // Two independent counters:
    //   _queuedCalls: SlowPath waiters, bounded by RequestQueueLimit.
    //   _sharedCalls: SharedPath in-flight, bounded by MaxConcurrentSharedRequests — caps memory
    //                 for heavy sharable methods (eth_call / eth_estimateGas / eth_createAccessList).
    public sealed class RpcLimits
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

        internal RpcLimits(int queuedLimit = 0, int sharedLimit = 0)
        {
            QueuedLimit = queuedLimit;
            SharedLimit = sharedLimit;
        }

        private int QueuedLimit { get; set; }
        private int SharedLimit { get; set; }
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
        private readonly ConcurrentQueue<Task<T>> _pool = new();
        private readonly ConcurrentDictionary<object, Task<T>> _exclusiveAsTasks = new(ReferenceEqualityComparer.Instance);
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
            _shared = factory.Create();
            _sharedAsTask = Task.FromResult(_shared);
        }

        public Task<T> GetModule(bool canBeShared) => canBeShared ? SharedPath() : ExclusivePath();

        private Task<T> SharedPath()
        {
            _limits.AcquireSharedSlot();
            return _sharedAsTask;
        }

        private Task<T> ExclusivePath() => _semaphore.Wait(0) ? TakeExclusive() : SlowPath();

        private async Task<T> SlowPath()
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

            return await TakeExclusive();
        }

        /// <remarks>
        /// Called with a semaphore slot held. <see cref="ReturnModule"/> queues an instance before it releases its slot,
        /// so an empty queue means every created instance is rented and fewer than the capacity exist.
        /// </remarks>
        private Task<T> TakeExclusive() => _pool.TryDequeue(out Task<T>? result) ? result : CreateExclusive();

        private Task<T> CreateExclusive()
        {
            T module;
            try
            {
                module = Factory.Create();
            }
            catch
            {
                _semaphore.Release();
                throw;
            }

            Task<T> exclusive = Task.FromResult(module);
            _exclusiveAsTasks[module] = exclusive;
            return exclusive;
        }

        /// <summary>Creates every exclusive instance up to the capacity, so later rentals do not pay for construction.</summary>
        public void Preload()
        {
            List<T> rented = [];
            try
            {
                while (_semaphore.Wait(0))
                {
                    rented.Add(TakeExclusive().Result);
                }
            }
            finally
            {
                foreach (T module in rented)
                {
                    ReturnModule(module);
                }
            }
        }

        public void ReturnModule(T module)
        {
            if (ReferenceEquals(module, _shared))
            {
                _limits.DecrementSharedCalls();
                return;
            }

            _pool.Enqueue(_exclusiveAsTasks.TryGetValue(module, out Task<T>? exclusive) ? exclusive : Task.FromResult(module));
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; }
    }
}
