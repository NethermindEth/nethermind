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

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Web3
{
    public class Web3RpcModule : IWeb3RpcModule
    {
        public Web3RpcModule(ILogManager logManager)
        {
        }

        public ResultWrapper<string> web3_clientVersion()
        {
            var clientVersion = ClientVersion.Description;
            return ResultWrapper<string>.Success(clientVersion);
        }

        public ResultWrapper<Keccak> web3_sha3(byte[] data)
        {
            Keccak keccak = Keccak.Compute(data);
            return ResultWrapper<Keccak>.Success(keccak);
        }
    }
}
