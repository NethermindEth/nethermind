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

using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public interface IDebugModule : IModule
    {
        ResultWrapper<TransactionTrace> debug_traceTransaction(Data transationHash);
        ResultWrapper<TransactionTrace> debug_traceTransactionByBlockAndIndex(BlockParameter blockParameter, int txIndex);
        ResultWrapper<TransactionTrace> debug_traceTransactionByBlockhashAndIndex(Data blockHash, int txIndex);
        ResultWrapper<bool> debug_addTxData(BlockParameter blockParameter);
        ResultWrapper<BlockTraceItem[]> debug_traceBlock(Data blockRlp);
        ResultWrapper<BlockTraceItem[]> debug_traceBlockByNumber(BlockParameter blockParameter);
        ResultWrapper<BlockTraceItem[]> debug_traceBlockByHash(Data blockHash);
        ResultWrapper<BlockTraceItem[]> debug_traceBlockFromFile(string fileName);
        ResultWrapper<State> debug_dumpBlock(BlockParameter blockParameter);
        ResultWrapper<GcStats> debug_gcStats();
        ResultWrapper<Data> debug_getBlockRlp(BlockParameter blockParameter);
        ResultWrapper<MemStats> debug_memStats(BlockParameter blockParameter);
        ResultWrapper<Data> debug_seedHash(BlockParameter blockParameter);
        ResultWrapper<bool> debug_setHead(BlockParameter blockParameter);
        ResultWrapper<byte[]> debug_getFromDb(string dbName, Data key);
    }
}