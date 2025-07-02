// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using Nethermind.Serialization.Json;

using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Modules
{
    public class NullModuleProvider : IRpcModuleProvider
    {
        public static NullModuleProvider Instance = new();
        private static readonly Task<IRpcModule> Null = Task.FromResult(default(IRpcModule));

        private NullModuleProvider()
        {
        }

        public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule
        {
        }

        public IJsonSerializer Serializer { get; } = new EthereumJsonSerializer();

        public IReadOnlyCollection<string> Enabled => Array.Empty<string>();

        public IReadOnlyCollection<string> All => Array.Empty<string>();

        public ModuleResolution Check(string methodName, JsonRpcContext context, out string? module)
        {
            module = null;
            return ModuleResolution.Unknown;
        }

        public ResolvedMethodInfo Resolve(string methodName) => new();

        public Task<IRpcModule> Rent(string methodName, bool canBeShared) => Null;

        public void Return(string methodName, IRpcModule rpcModule) { }

        public IRpcModulePool? GetPoolForMethod(string moduleType) => null;
    }
}
