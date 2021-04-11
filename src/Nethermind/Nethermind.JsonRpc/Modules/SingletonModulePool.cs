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
using System.Threading.Tasks;

namespace Nethermind.JsonRpc.Modules
{
    public class SingletonModulePool<T> : IRpcModulePool<T> where T : IRpcModule
    {
        private readonly T _onlyInstance;
        private readonly Task<T> _onlyInstanceAsTask;
        private readonly bool _allowExclusive;

        public SingletonModulePool(T module, bool allowExclusive = true)
            : this(new SingletonFactory<T>(module), allowExclusive) { }

        public SingletonModulePool(IRpcModuleFactory<T> factory, bool allowExclusive = true)
        {
            Factory = factory;
            _onlyInstance = factory.Create();
            _onlyInstanceAsTask = Task.FromResult(_onlyInstance);
            _allowExclusive = allowExclusive;
        }

        public Task<T> GetModule(bool canBeShared)
        {
            if (!canBeShared && !_allowExclusive)
            {
                throw new InvalidOperationException($"{nameof(SingletonModulePool<T>)} can only return shareable modules");
            }

            return _onlyInstanceAsTask;
        }

        public void ReturnModule(T module)
        {
        }

        public IRpcModuleFactory<T> Factory { get; set; }
    }
}
