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

namespace Nethermind.JsonRpc.Modules.TxPool
{
    [RpcModule(ModuleType.TxPool)]
    public interface ITxPoolRpcModule : IRpcModule
    {
        [JsonRpcMethod(Description = "Returns a tx pool status.", IsImplemented = true)]
        ResultWrapper<TxPoolStatus> txpool_status();
        
        [JsonRpcMethod(Description = "Returns tx pool content.", IsImplemented = true)]
        ResultWrapper<TxPoolContent> txpool_content();
        
        [JsonRpcMethod(Description = "Returns a detailed info on tx pool transactions.", IsImplemented = true)]
        ResultWrapper<TxPoolInspection> txpool_inspect();

        [JsonRpcMethod(Description = "Returns string (CSV ready to paste in calc sheet) with all txs in txpool with details: txHash, senderAddress, current nonce of senderAddress, tx nonce, nonces difference, timestamp",
            IsImplemented = false)]
        ResultWrapper<string> txpool_snapshot();
    }
}
