/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Nethermind.JsonRpc.Modules
{
    public class BoundedModulePool<T> : IRpcModulePool<T> where T : IModule
    {
        private T _shared;
        private ConcurrentBag<T> _bag = new ConcurrentBag<T>();
        private SemaphoreSlim _semaphore;

        public BoundedModulePool(int exclusiveCapacity, IRpcModuleFactory<T> factory)
        {
            Factory = factory;
            
            _semaphore = new SemaphoreSlim(exclusiveCapacity);
            for (int i = 0; i < exclusiveCapacity; i++)
            {
                _bag.Add(Factory.Create());
            }

            _shared = factory.Create();
        }
        
        public T GetModule(bool canBeShared)
        {
            if (canBeShared)
            {
                return _shared;
            }
            
            if (!_semaphore.Wait(10000))
            {
                throw new TimeoutException($"Unable to rent an instance of {typeof(T).Name}");
            }

            _bag.TryTake(out T result);
            return result;
        }

        public void ReturnModule(T module)
        {
            _bag.Add(module);
            _semaphore.Release();
        }

        public IRpcModuleFactory<T> Factory { get; set; }
    }
}