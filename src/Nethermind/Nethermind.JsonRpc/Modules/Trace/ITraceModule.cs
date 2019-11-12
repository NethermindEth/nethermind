/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;

namespace Nethermind.JsonRpc.Modules.Trace
{
    [RpcModule(ModuleType.Trace)]
    public interface ITraceModule : IModule
    {
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace> trace_call(TransactionForRpc message, string[] traceTypes, BlockParameter numberOrTag);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace[]> trace_callMany((TransactionForRpc message, string[] traceTypes, BlockParameter numberOrTag)[] calls);
        
        [JsonRpcMethod(Description = "Traces a call to eth_sendRawTransaction without making the call, returning the traces", IsImplemented = true, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace> trace_rawTransaction(byte[] data, string[] traceTypes);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace> trace_replayTransaction(Keccak txHash, string[] traceTypes);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace[]> trace_replayBlockTransactions(BlockParameter numberOrTag, string[] traceTypes);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace[]> trace_filter(BlockParameter fromBlock, BlockParameter toBlock, Address toAddress, int after, int count);
        
        [JsonRpcMethod(Description = "", IsImplemented = true, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace[]> trace_block(BlockParameter numberOrTag);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace> trace_get(Keccak txHash, int[] positions);
        
        [JsonRpcMethod(Description = "", IsImplemented = true, IsReadOnly = false)]
        ResultWrapper<ParityLikeTxTrace> trace_transaction(Keccak txHash);
    }
}