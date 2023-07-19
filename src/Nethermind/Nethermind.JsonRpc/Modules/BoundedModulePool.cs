// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;
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
        private readonly ILogger _logger;
        private int _activeWorkers = 0;
        private int _rpcPendingCalls = 0;
        private readonly int _exclusiveCapacity = 0;
        private readonly int _maxPendingSharedRequests = 0;
        private readonly bool _pendingRequestLimitsEnabled = false;

        public BoundedModulePool(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout, ILogManager logManager, int maxPendingSharedRequests = 0)
        {
            _maxPendingSharedRequests = maxPendingSharedRequests;
            _pendingRequestLimitsEnabled = _maxPendingSharedRequests > 0;
            _exclusiveCapacity = exclusiveCapacity;
            _logger = logManager.GetClassLogger();
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
            if (_pendingRequestLimitsEnabled && _rpcPendingCalls > _maxPendingSharedRequests)
            {
                throw new LimitExceededException($"Unable to start new pending requests for {typeof(T).Name}. Too many pending requests. Pending calls {_rpcPendingCalls}.");
            }

            IncrementRpcPendingCalls();
            if (_logger.IsTrace)
                _logger.Trace($"{typeof(T).Name} Pending RPC requests {_rpcPendingCalls} Active Workers {_activeWorkers}/{_exclusiveCapacity}");

            if (!await _semaphore.WaitAsync(_timeout))
            {
                DecrementRpcPendingCalls();
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

            DecrementRpcPendingCalls();
            ++_activeWorkers;
            _pool.TryDequeue(out T result);
            return result;
        }

        private void IncrementRpcPendingCalls()
        {
            if (_pendingRequestLimitsEnabled)
                Interlocked.Increment(ref _rpcPendingCalls);
        }

        private void DecrementRpcPendingCalls()
        {
            if (_pendingRequestLimitsEnabled)
                Interlocked.Decrement(ref _rpcPendingCalls);
        }

        public void ReturnModule(T module)
        {
            if (ReferenceEquals(module, _shared))
            {
                return;
            }

            --_activeWorkers;
            _pool.Enqueue(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; }
    }
}
