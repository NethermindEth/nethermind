// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;

using Nethermind.Serialization.Json;
using static Nethermind.JsonRpc.Modules.RpcModuleProvider;

namespace Nethermind.JsonRpc.Modules
{
    // ReSharper disable once InconsistentNaming

    public interface IRpcModuleProvider
    {
        void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule;

        IReadOnlyCollection<string> Enabled { get; }

        IReadOnlyCollection<string> All { get; }
        IJsonSerializer Serializer { get; }

        ModuleResolution Check(string methodName, JsonRpcContext context, out string? module, out ResolvedMethodInfo? method);
        ModuleResolution Check(string methodName, JsonRpcContext context, out string? module) => Check(methodName, context, out module, out _);
        ModuleResolution Check(string methodName, JsonRpcContext context) => Check(methodName, context, out _, out _);

        ResolvedMethodInfo? Resolve(string methodName);

        ValueTask<IRpcModule> Rent(string methodName, bool canBeShared);
        ValueTask<IRpcModule> Rent(ResolvedMethodInfo method);

        void Return(string methodName, IRpcModule rpcModule);
        void Return(ResolvedMethodInfo method, IRpcModule rpcModule);
    }
}
