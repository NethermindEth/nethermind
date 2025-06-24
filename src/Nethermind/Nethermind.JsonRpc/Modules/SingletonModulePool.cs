// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

        public SingletonModulePool(IRpcModuleFactory<T> factory, bool allowExclusive = true, bool allowContextAwareRpc = false)
        {
            Factory = factory;
            _onlyInstance = factory.Create();
            _onlyInstanceAsTask = Task.FromResult(_onlyInstance);
            _allowExclusive = allowExclusive;

            if (_onlyInstance is IContextAwareRpcModule && !allowContextAwareRpc)
            {
                throw new InvalidOperationException(
                    $"{_onlyInstance} implement {nameof(IContextAwareRpcModule)} which is not safe to share and should not use singleton module pool.");
            }
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

        public IRpcModuleFactory<T> Factory { get; }
    }
}
