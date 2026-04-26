// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;

namespace Nethermind.JsonRpc.Modules
{
    public abstract class ModuleFactoryBase<T> : IRpcModuleFactory<T> where T : IRpcModule
    {
        public ModuleFactoryBase()
        {
            if (!typeof(T).IsInterface)
            {
                throw new InvalidOperationException($"Module factory type should be an interface and not {typeof(T).Name}");
            }

            RpcModuleAttribute attribute = typeof(T).GetCustomAttribute<RpcModuleAttribute>() ?? throw new InvalidOperationException($"RPC module {typeof(T).Name} is missing {nameof(RpcModuleAttribute)}");
            ModuleType = attribute.ModuleType;
        }

        public abstract T Create();

        public string ModuleType { get; }
    }

    public class SingletonFactory<T>(T module) : ModuleFactoryBase<T> where T : IRpcModule
    {
        private readonly T _module = module;

        public override T Create() => _module;
    }
}
