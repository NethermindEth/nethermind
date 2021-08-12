//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
            if (! await _semaphore.WaitAsync(_timeout))
            {
                throw new ModuleRentalTimeoutException($"Unable to rent an instance of {typeof(T).Name}. Too many concurrent requests.");
            }

            _pool.TryDequeue(out T result);
            return result;
        }

        public void ReturnModule(T module)
        {
            if(ReferenceEquals(module, _shared))
            {
                return;
            }
            
            _pool.Enqueue(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; set; }
    }
}
