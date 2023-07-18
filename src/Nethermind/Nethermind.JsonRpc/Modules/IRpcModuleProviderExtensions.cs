// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules
{
    public static class IRpcModuleProviderExtensions
    {
        private static readonly int _cpuCount = Environment.ProcessorCount;

        public static void RegisterBounded<T>(
            this IRpcModuleProvider rpcModuleProvider,
            ModuleFactoryBase<T> factory,
            int maxCount,
            int timeout,
            ILogManager logManager,
            int maxPendingSharedRequests = 0)
            where T : IRpcModule
        {
            rpcModuleProvider.Register(new BoundedModulePool<T>(factory, maxCount, timeout, logManager, maxPendingSharedRequests));
        }

        public static void RegisterBoundedByCpuCount<T>(
            this IRpcModuleProvider rpcModuleProvider,
            ModuleFactoryBase<T> factory,
            int timeout,
            ILogManager logManager)
            where T : IRpcModule
        {
            RegisterBounded(rpcModuleProvider, factory, _cpuCount, timeout, logManager);
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
