// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.JsonRpc.Modules
{
    public static class IRpcModuleProviderExtensions
    {
        private static readonly int _cpuCount = Environment.ProcessorCount;

        public static void RegisterBounded<T>(
            this IRpcModuleProvider rpcModuleProvider,
            ModuleFactoryBase<T> factory,
            int maxCount,
            int timeout)
            where T : IRpcModule
        {
            rpcModuleProvider.Register(new BoundedModulePool<T>(factory, maxCount, timeout));
        }

        public static void RegisterBoundedByCpuCount<T>(
            this IRpcModuleProvider rpcModuleProvider,
            ModuleFactoryBase<T> factory,
            int timeout)
            where T : IRpcModule
        {
            RegisterBounded(rpcModuleProvider, factory, _cpuCount, timeout);
        }

        public static void RegisterSingle<T>(
            this IRpcModuleProvider rpcModuleProvider,
            T module,
            bool allowExclusive = true)
            where T : IRpcModule
        {
            rpcModuleProvider.Register(new SingletonModulePool<T>(module, allowExclusive));
        }
    }
}
