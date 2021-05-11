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

        public IReadOnlyCollection<JsonConverter> Converters => Array.Empty<JsonConverter>();
        
        public IReadOnlyCollection<string> Enabled => Array.Empty<string>();
        
        public IReadOnlyCollection<string> All => Array.Empty<string>();
        
        public ModuleResolution Check(string methodName, RpcEndpoint rpcEndpoint)
        {
            return ModuleResolution.Unknown;
        }

        public (MethodInfo, bool) Resolve(string methodName)
        {
            return (null, false);
        }

        public Task<IRpcModule> Rent(string methodName, bool canBeShared)
        {
            return Null;
        }

        public void Return(string methodName, IRpcModule rpcModule)
        {
        }
    }
}
