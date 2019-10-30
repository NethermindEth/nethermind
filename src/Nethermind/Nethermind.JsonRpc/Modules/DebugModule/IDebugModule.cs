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

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.Data;
using Nethermind.JsonRpc.Modules.Eth;

namespace Nethermind.JsonRpc.Modules.DebugModule
{
    [RpcModule(ModuleType.Debug)]
    public interface IDebugModule : IModule
    {
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Keccak transactionHash, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int txIndex, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Keccak blockHash, int txIndex, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlock(byte[] blockRlp, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByNumber(BigInteger number, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByHash(Keccak blockHash, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<State> debug_dumpBlock(BlockParameter blockParameter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<GcStats> debug_gcStats();
        
        [JsonRpcMethod(Description = "Retrieves a block in the RLP-serialized form.", IsReadOnly = true)]
        ResultWrapper<byte[]> debug_getBlockRlp(long number);
        
        [JsonRpcMethod(Description = "Retrieves a block in the RLP-serialized form.", IsReadOnly = false)]
        ResultWrapper<byte[]> debug_getBlockRlpByHash(Keccak hash);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter);

        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = false)]
        ResultWrapper<bool> debug_setHead(BlockParameter blockParameter);
        
        [JsonRpcMethod(Description = "", IsImplemented = false, IsReadOnly = true)]
        ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key);
        
        [JsonRpcMethod(Description = "Retrieves the Nethermind configuration value, e.g. JsonRpc.Enabled", IsReadOnly = true)]
        ResultWrapper<string> debug_getConfigValue(string category, string name);

        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByHash(byte[] blockRlp, Keccak transactionHash, GethTraceOptions options = null);
        
        [JsonRpcMethod(Description = "", IsReadOnly = false)]
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionInBlockByIndex(byte[] blockRlp, int txIndex, GethTraceOptions options = null);
    }
}