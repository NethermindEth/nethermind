﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing.GethStyle;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    [RpcModule(ModuleType.Debug)]
    public interface IDebugModule : IModule
    {
        [JsonRpcMethod(Description = "Retrieves a representation of tree branches on a given chain level (Nethermind specific).", IsReadOnly = true)]
        ResultWrapper<ChainLevelForRpc> debug_getChainLevel(in long number);
        
        [JsonRpcMethod(Description = "Deletes a slice of a chain from the tree on all branches (Nethermind specific).", IsReadOnly = true)]
        ResultWrapper<int> debug_deleteChainSlice(in long startNumber);
        
        [JsonRpcMethod(
            Description = "Updates / resets head block - use only when the node got stuck due to DB / memory corruption (Nethermind specific).",
            IsReadOnly = true)]
        ResultWrapper<bool> debug_resetHead(Keccak blockHash);
        
        [JsonRpcMethod(Description = "", IsReadOnly = true)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Keccak transactionHash, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = true)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int txIndex, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = true)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Keccak blockHash, int txIndex, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = true)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = true)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByNumber(UInt256 number, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = true)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByHash(Keccak blockHash, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<object> debug_dumpBlock(BlockParameter blockParameter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<GcStats> debug_gcStats();
        
        [JsonRpcMethod(Description = "Retrieves a block in the RLP-serialized form.", IsImplemented = true, IsReadOnly = true)]
        ResultWrapper<byte[]> debug_getBlockRlp(long number);
        
        [JsonRpcMethod(Description = "Retrieves a block in the RLP-serialized form.", IsImplemented = true, IsReadOnly = false)]
        ResultWrapper<byte[]> debug_getBlockRlpByHash(Keccak hash);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter);

        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<bool> debug_setHead(BlockParameter blockParameter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key);
        
        [JsonRpcMethod(Description = "Retrieves the Nethermind configuration value, e.g. JsonRpc.Enabled", IsImplemented = true, IsReadOnly = true)]
        ResultWrapper<object> debug_getConfigValue(string category, string name);

        [JsonRpcMethod(Description = "", IsImplemented = true, IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Keccak transactionHash, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsImplemented = true, IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "Sets the block number up to which receipts will be migrated to (Nethermind specific).")]
        Task<ResultWrapper<bool>> debug_migrateReceipts(long blockNumber);
    }
}
