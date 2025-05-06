// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO.Abstractions;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.JsonRpc.Modules.Net;
using Nethermind.JsonRpc.Modules.Parity;
using Nethermind.JsonRpc.Modules.Trace;
using Nethermind.JsonRpc.Modules.Web3;
using Nethermind.Logging;
using Nethermind.Serialization.Json;


using NSubstitute;

using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Test.Modules
{
    internal class TestRpcModuleProvider<T> : IRpcModuleProvider where T : class, IRpcModule
    {
        private readonly JsonRpcConfig _jsonRpcConfig;
        private readonly RpcModuleProvider _provider;

        public TestRpcModuleProvider(T module)
        {
            _jsonRpcConfig = new JsonRpcConfig();
            _provider = new RpcModuleProvider(new FileSystem(), _jsonRpcConfig, new EthereumJsonSerializer(), LimboLogs.Instance);

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
                    _jsonRpcConfig.EnabledModules = _jsonRpcConfig.EnabledModules.Union([rpcModuleAttribute.ModuleType]).ToArray();
                }
            }
        }

        public IJsonSerializer Serializer => _provider.Serializer;
        public IReadOnlyCollection<string> Enabled => _provider.All;
        public IReadOnlyCollection<string> All => _provider.All;
        public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module) => _provider.Check(methodName, context, out module);

        public ResolvedMethodInfo? Resolve(string methodName) => _provider.Resolve(methodName);

        public Task<IRpcModule> Rent(string methodName, bool readOnly) => _provider.Rent(methodName, readOnly);

        public void Return(string methodName, IRpcModule rpcModule) => _provider.Return(methodName, rpcModule);

        public IRpcModulePool? GetPoolForMethod(string methodName) => _provider.GetPoolForMethod(methodName);
    }
}
