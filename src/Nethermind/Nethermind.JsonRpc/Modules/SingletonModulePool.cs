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

namespace Nethermind.JsonRpc.Modules
{
    public class SingletonModulePool<T> : IRpcModulePool<T> where T : IModule
    {
        private readonly T _onlyInstance;
        private readonly bool _allowExclusive;

        public SingletonModulePool(T module, bool allowExclusive)
        {
            Factory = new SingletonFactory<T>(module);
            _onlyInstance = module;
            _allowExclusive = allowExclusive;
        }

        public SingletonModulePool(IRpcModuleFactory<T> factory, bool allowExclusive)
        {
            Factory = factory;
            _onlyInstance = factory.Create();
            _allowExclusive = allowExclusive;
        }
        
        public T GetModule(bool canBeShared)
        {
            if (!canBeShared && !_allowExclusive)
            {
                throw new InvalidOperationException($"{nameof(SingletonModulePool<T>)} can only return shareable modules");
            }
            
            return _onlyInstance;
        }

        public void ReturnModule(T module)
        {
        }

        public IRpcModuleFactory<T> Factory { get; set; }
    }
}