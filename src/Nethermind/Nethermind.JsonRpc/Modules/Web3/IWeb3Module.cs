﻿//  Copyright (c) 2021 Demerzel Solutions Limited
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

using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Modules.Web3
{
    [RpcModule(ModuleType.Web3)]
    public interface IWeb3Module : IModule
    {
        [JsonRpcMethod(Description = "Returns the current client version.", IsImplemented = true)]
        ResultWrapper<string> web3_clientVersion();

        [JsonRpcMethod(Description = "Returns Keccak of the given data.", IsImplemented = true)]
        ResultWrapper<Keccak> web3_sha3(byte[] data);
    }
}
