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
            ResponseDescription = "Keccak representation of Witness",
            ExampleResponse =
                "[\"0xe686ead4169241f99b318240a0557ab5dd4d99444367ca4a28e60cda5717ef2c\"]",
            IsImplemented = true)]
        Task<ResultWrapper<Keccak[]>> get_witnesses([JsonRpcParameter(Description = "Block to get witness",
                ExampleValue = "[\"8934677\"]")]
            string blockHash);
    }
}
