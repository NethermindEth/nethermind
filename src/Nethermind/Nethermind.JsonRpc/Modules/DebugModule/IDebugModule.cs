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
    public interface IDebugModule : IModule
    {
        ResultWrapper<GethLikeTxTrace> debug_traceTransaction(Keccak transactionHash);
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int txIndex);
        ResultWrapper<GethLikeTxTrace> debug_traceTransactionByBlockhashAndIndex(Keccak blockHash, int txIndex);
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlock(byte[] blockRlp);
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByNumber(BigInteger number);
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockByHash(Keccak blockHash);
        ResultWrapper<GethLikeTxTrace[]> debug_traceBlockFromFile(string fileName);
        ResultWrapper<State> debug_dumpBlock(BlockParameter blockParameter);
        ResultWrapper<GcStats> debug_gcStats();
        ResultWrapper<byte[]> debug_getBlockRlp(BlockParameter blockParameter);
        ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter);
        ResultWrapper<byte[]> debug_seedHash(BlockParameter blockParameter);
        ResultWrapper<bool> debug_setHead(BlockParameter blockParameter);
        ResultWrapper<byte[]> debug_getFromDb(string dbName, byte[] key);
        ResultWrapper<string> debug_getConfigValue(string category, string name);
    }
}