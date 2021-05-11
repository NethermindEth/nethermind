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
using System.IO.Abstractions;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
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
    internal class TestRpcModuleProvider<T> : IRpcModuleProvider where T : class, IRpcModule
    {
        private readonly JsonRpcConfig _jsonRpcConfig;
        private readonly RpcModuleProvider _provider;
        

        public TestRpcModuleProvider(T module)
        {
            _jsonRpcConfig = new JsonRpcConfig();
            _provider = new RpcModuleProvider(new FileSystem(), _jsonRpcConfig, LimboLogs.Instance);
            
            _provider.Register(new SingletonModulePool<INetRpcModule>(new SingletonFactory<INetRpcModule>(typeof(INetRpcModule).IsAssignableFrom(typeof(T)) ? (INetRpcModule)module : Substitute.For<INetRpcModule>()), true));
            _provider.Register(new SingletonModulePool<IEthRpcModule>(new SingletonFactory<IEthRpcModule>(typeof(IEthRpcModule).IsAssignableFrom(typeof(T)) ? (IEthRpcModule)module : Substitute.For<IEthRpcModule>()), true));
            _provider.Register(new SingletonModulePool<IWeb3RpcModule>(new SingletonFactory<IWeb3RpcModule>(typeof(IWeb3RpcModule).IsAssignableFrom(typeof(T)) ? (IWeb3RpcModule)module : Substitute.For<IWeb3RpcModule>()), true));
            _provider.Register(new SingletonModulePool<IDebugRpcModule>(new SingletonFactory<IDebugRpcModule>(typeof(IDebugRpcModule).IsAssignableFrom(typeof(T)) ? (IDebugRpcModule)module : Substitute.For<IDebugRpcModule>()), true));
            _provider.Register(new SingletonModulePool<ITraceRpcModule>(new SingletonFactory<ITraceRpcModule>(typeof(ITraceRpcModule).IsAssignableFrom(typeof(T)) ? (ITraceRpcModule)module : Substitute.For<ITraceRpcModule>()), true));
            _provider.Register(new SingletonModulePool<IParityRpcModule>(new SingletonFactory<IParityRpcModule>(typeof(IParityRpcModule).IsAssignableFrom(typeof(T)) ? (IParityRpcModule)module : Substitute.For<IParityRpcModule>()), true));
        }

        public void Register<TOther>(IRpcModulePool<TOther> pool) where TOther : IRpcModule
        {
            EnableModule<TOther>();
            _provider.Register(pool);
        }

        private void EnableModule<TOther>() where TOther : IRpcModule
        {
            if (Attribute.GetCustomAttribute(typeof(TOther), typeof(RpcModuleAttribute), true) is RpcModuleAttribute rpcModuleAttribute)
            {
                if (!_jsonRpcConfig.EnabledModules.Contains(rpcModuleAttribute.ModuleType))
                {
                    _jsonRpcConfig.EnabledModules = _jsonRpcConfig.EnabledModules.Union(new[] {rpcModuleAttribute.ModuleType}).ToArray();
                }
            }
        }

        public IReadOnlyCollection<JsonConverter> Converters => _provider.Converters;
        public IReadOnlyCollection<string> Enabled => _provider.All;
        public IReadOnlyCollection<string> All => _provider.All;
        public ModuleResolution Check(string methodName, RpcEndpoint rpcEndpoint)
        {
            return _provider.Check(methodName, rpcEndpoint);
        }

        public (MethodInfo, bool) Resolve(string methodName)
        {
            return _provider.Resolve(methodName);
        }

        public Task<IRpcModule> Rent(string methodName, bool readOnly)
        {
            return _provider.Rent(methodName, readOnly);
        }

        public void Return(string methodName, IRpcModule rpcModule)
        {
            _provider.Return(methodName, rpcModule);
        }
    }
}
