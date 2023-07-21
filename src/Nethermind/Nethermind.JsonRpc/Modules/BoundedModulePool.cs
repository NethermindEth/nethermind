// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Exceptions;
using Nethermind.Logging;
using ILogger = Nethermind.Logging.ILogger;

namespace Nethermind.JsonRpc.Modules
{
    public static class RpcLimits
    {
        public static void Init(int limit)
        {
            Limit = limit;
        }

        private static int Limit { get; set; }
        private static bool Enabled => Limit > 0;
        private static int _queuedCalls = 0;
        public static int QueuedCalls => _queuedCalls;

        public static void IncrementQueuedCalls()
        {
            if (Enabled)
                Interlocked.Increment(ref _queuedCalls);
        }

        public static void DecrementQueuedCalls()
        {
            if (Enabled)
                Interlocked.Decrement(ref _queuedCalls);
        }

        public static void EnsureLimits()
        {
            if (Enabled && _queuedCalls > Limit)
            {
                throw new LimitExceededException($"Unable to start new queued requests. Too many queued requests. Queued calls {_queuedCalls}.");
            }
        }
    }
    public class BoundedModulePool<T> : IRpcModulePool<T> where T : IRpcModule
    {
        private readonly int _timeout;
        private readonly T _shared;
        private readonly Task<T> _sharedAsTask;
        private readonly ConcurrentQueue<T> _pool = new();
        private readonly SemaphoreSlim _semaphore;
        private readonly ILogger _logger;

        public BoundedModulePool(IRpcModuleFactory<T> factory, int exclusiveCapacity, int timeout, ILogManager logManager)
        {
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
            RpcLimits.EnsureLimits();
            RpcLimits.IncrementQueuedCalls();
            if (_logger.IsTrace)
                _logger.Trace($"{typeof(T).Name} Queued RPC requests {RpcLimits.QueuedCalls}");

            if (!await _semaphore.WaitAsync(_timeout))
            {
                RpcLimits.DecrementQueuedCalls();
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

            RpcLimits.DecrementQueuedCalls();
            _pool.TryDequeue(out T result);
            return result;
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
