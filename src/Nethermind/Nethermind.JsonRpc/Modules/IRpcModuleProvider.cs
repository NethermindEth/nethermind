// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

using Nethermind.Serialization.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nethermind.JsonRpc.Modules
{
    // ReSharper disable once InconsistentNaming

    public interface IRpcModuleProvider
    {
        void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule;

        IReadOnlyCollection<string> Enabled { get; }

        IReadOnlyCollection<string> All { get; }
        IJsonSerializer Serializer { get; }

        ModuleResolution Check(string methodName, JsonRpcContext context);

        (MethodInfo MethodInfo, ParameterInfo[], bool ReadOnly) Resolve(string methodName);

        Task<IRpcModule> Rent(string methodName, bool canBeShared);

        void Return(string methodName, IRpcModule rpcModule);

        IRpcModulePool? GetPool(string moduleType);
    }
}
