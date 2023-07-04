// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules
{
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

        public Task<T> GetModule(bool canBeShared) => canBeShared ? _sharedAsTask : SlowPath();

        private async Task<T> SlowPath()
        {
            if (!await _semaphore.WaitAsync(_timeout))
            {
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

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
