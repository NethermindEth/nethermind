// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.JsonRpc.Modules
{
    public class BoundedModulePool<T> : IRpcModulePool<T> where T : IRpcModule
    {
        private readonly int _timeout;
        private readonly T _shared;
        private readonly Task<T> _sharedAsTask;
        private readonly ConcurrentQueue<T> _pool = new();
        private readonly SemaphoreSlim _semaphore;
        private int _rpcQueuedCalls = 0;
        private readonly int _requestQueueLimit = 0;
        private bool RequestLimitEnabled => _requestQueueLimit > 0;

        public BoundedModulePool(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout, int requestQueueLimit = 0)
        {
            _requestQueueLimit = requestQueueLimit;
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

        public Task<T> GetModule(bool canBeShared) => canBeShared ? _sharedAsTask : SlowPath();

        private async Task<T> SlowPath()
        {
            if (RequestLimitEnabled && _rpcQueuedCalls > _requestQueueLimit)
            {
                throw new LimitExceededException($"Unable to start new queued requests for {typeof(T).Name}. Too many queued requests. Queued calls {_rpcQueuedCalls}.");
            }

            IncrementRpcQueuedCalls();

            if (!await _semaphore.WaitAsync(_timeout))
            {
                DecrementRpcQueuedCalls();
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

            DecrementRpcQueuedCalls();
            _pool.TryDequeue(out T result);
            return result;
        }

        private void IncrementRpcQueuedCalls()
        {
            if (RequestLimitEnabled)
                Interlocked.Increment(ref _rpcQueuedCalls);
        }

        private void DecrementRpcQueuedCalls()
        {
            if (RequestLimitEnabled)
                Interlocked.Decrement(ref _rpcQueuedCalls);
        }

        public void ReturnModule(T module)
        {
            if (ReferenceEquals(module, _shared))
            {
                return;
            }

            _pool.Enqueue(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; }
    }
}
