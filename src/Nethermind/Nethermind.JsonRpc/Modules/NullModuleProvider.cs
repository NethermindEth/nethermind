// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules
{
    public class NullModuleProvider : IRpcModuleProvider
    {
        public static NullModuleProvider Instance = new();
        private static Task<IRpcModule> Null = Task.FromResult(default(IRpcModule));

        private NullModuleProvider()
        {
        }

        public void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule
        {
        }

        public JsonSerializer Serializer { get; } = new();

        public IReadOnlyCollection<JsonConverter> Converters => Array.Empty<JsonConverter>();

        public IReadOnlyCollection<string> Enabled => Array.Empty<string>();

        public IReadOnlyCollection<string> All => Array.Empty<string>();

        public ModuleResolution Check(string methodName, JsonRpcContext context) => ModuleResolution.Unknown;

        public (MethodInfo, bool) Resolve(string methodName) => (null, false);

        public Task<IRpcModule> Rent(string methodName, bool canBeShared) => Null;

        public void Return(string methodName, IRpcModule rpcModule) { }

        public IRpcModulePool? GetPool(string moduleType) => null;
    }
}
