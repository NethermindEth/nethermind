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

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Modules
{
    // ReSharper disable once InconsistentNaming

    public interface IRpcModuleProvider
    {
        void Register<T>(IRpcModulePool<T> pool) where T : IRpcModule;

        IReadOnlyCollection<JsonConverter> Converters { get; }

        IReadOnlyCollection<string> Enabled { get; }
        
        IReadOnlyCollection<string> All { get; }

        ModuleResolution Check(string methodName, RpcEndpoint rpcEndpoint);
        
        (MethodInfo MethodInfo, bool ReadOnly) Resolve(string methodName);
        
        Task<IRpcModule> Rent(string methodName, bool canBeShared);
        
        void Return(string methodName, IRpcModule rpcModule);
    }
}
