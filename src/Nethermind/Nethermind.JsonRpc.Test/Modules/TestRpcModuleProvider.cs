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

using System.Collections.Generic;
using System.Reflection;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Newtonsoft.Json;
using NSubstitute;

namespace Nethermind.JsonRpc.Test.Modules
{
    internal class TestRpcModuleProvider<T> : IRpcModuleProvider where T : class, IModule
    {
        private RpcModuleProvider _provider = new RpcModuleProvider(new JsonRpcConfig(), LimboLogs.Instance);

        public TestRpcModuleProvider(T module)
        {
            _provider.Register(new SingletonModulePool<INetModule>(new SingletonFactory<INetModule>(typeof(INetModule).IsAssignableFrom(typeof(T)) ? (INetModule)module : Substitute.For<INetModule>()), true));
            _provider.Register(new SingletonModulePool<IEthModule>(new SingletonFactory<IEthModule>(typeof(IEthModule).IsAssignableFrom(typeof(T)) ? (IEthModule)module : Substitute.For<IEthModule>()), true));
            _provider.Register(new SingletonModulePool<IWeb3Module>(new SingletonFactory<IWeb3Module>(typeof(IWeb3Module).IsAssignableFrom(typeof(T)) ? (IWeb3Module)module : Substitute.For<IWeb3Module>()), true));
            _provider.Register(new SingletonModulePool<IDebugModule>(new SingletonFactory<IDebugModule>(typeof(IDebugModule).IsAssignableFrom(typeof(T)) ? (IDebugModule)module : Substitute.For<IDebugModule>()), true));
            _provider.Register(new SingletonModulePool<ITraceModule>(new SingletonFactory<ITraceModule>(typeof(ITraceModule).IsAssignableFrom(typeof(T)) ? (ITraceModule)module : Substitute.For<ITraceModule>()), true));
            _provider.Register(new SingletonModulePool<IParityModule>(new SingletonFactory<IParityModule>(typeof(IParityModule).IsAssignableFrom(typeof(T)) ? (IParityModule)module : Substitute.For<IParityModule>()), true));
        }

        public void Register<TOther>(IRpcModulePool<TOther> pool) where TOther : IModule
        {
            _provider.Register(pool);
        }

        public IReadOnlyCollection<JsonConverter> Converters => _provider.Converters;
        public IReadOnlyCollection<ModuleType> Enabled => _provider.All;
        public IReadOnlyCollection<ModuleType> All => _provider.All;
        public ModuleResolution Check(string methodName)
        {
            return _provider.Check(methodName);
        }

        public (MethodInfo, bool) Resolve(string methodName)
        {
            return _provider.Resolve(methodName);
        }

        public IModule Rent(string methodName, bool readOnly)
        {
            return _provider.Rent(methodName, readOnly);
        }

        public void Return(string methodName, IModule module)
        {
            _provider.Return(methodName, module);
        }
    }
}