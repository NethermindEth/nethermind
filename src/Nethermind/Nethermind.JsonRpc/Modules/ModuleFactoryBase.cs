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
using System.Collections.Generic;
using System.Reflection;
using Newtonsoft.Json;

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

            RpcModuleAttribute attribute = typeof(T).GetCustomAttribute<RpcModuleAttribute>();
            if (attribute == null)
            {
                throw new InvalidOperationException($"RPC module {typeof(T).Name} is missing {nameof(RpcModuleAttribute)}");
            }

            ModuleType = attribute.ModuleType;
        }

        // ReSharper disable once StaticMemberInGenericType
        private static IReadOnlyCollection<JsonConverter> _noConverters = new List<JsonConverter>();

        public abstract T Create();

        public string ModuleType { get; }

        public virtual IReadOnlyCollection<JsonConverter> GetConverters()
        {
            return _noConverters;
        }
    }

    public class SingletonFactory<T> : ModuleFactoryBase<T> where T : IRpcModule
    {
        private readonly T _module;

        public SingletonFactory(T module)
        {
            _module = module;
        }

        public override T Create()
        {
            return _module;
        }
    }
}
