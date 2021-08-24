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
// 

using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Witness
{
    [RpcModule(ModuleType.Witness)]
    public interface IWitnessRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Return witness of Block provided",
            ResponseDescription = "Table of hashes of state nodes that were read during block processing",
            ExampleResponse =
                "\"0x1\"",
            IsImplemented = true)]
        Task<ResultWrapper<Keccak[]>> get_witnesses([JsonRpcParameter(Description = "Block to get witness",
                ExampleValue = "{\"jsonrpc\":\"2.0\",\"result\":[\"0xa2a9f03b9493046696099d27b2612b99497aa1f392ec966716ab393c715a5bb6\"],\"id\":67}")]
            BlockParameter blockParameter);
    }
}
