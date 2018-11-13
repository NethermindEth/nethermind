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
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;
using Nethermind.JsonRpc.DataModel;
using Block = Nethermind.JsonRpc.DataModel.Block;
using Transaction = Nethermind.JsonRpc.DataModel.Transaction;
using TransactionReceipt = Nethermind.JsonRpc.DataModel.TransactionReceipt;
using TransactionTrace = Nethermind.JsonRpc.DataModel.TransactionTrace;

namespace Nethermind.JsonRpc
{
    public interface IJsonRpcModelMapper
    {
        Block MapBlock(Core.Block block, bool returnFullTransactionObjects);
        Transaction MapTransaction(Keccak blockHash, UInt256 blockNumber, int index, Core.Transaction transaction);
        Transaction MapTransaction(Core.TransactionReceipt receipt, Core.Transaction transaction);
        Core.Transaction MapTransaction(Transaction transaction);
        TransactionReceipt MapTransactionReceipt(Keccak txHash, Core.TransactionReceipt receipt);
        TransactionTrace MapTransactionTrace(Evm.Tracing.TransactionTrace transactionTrace);
        BlockTraceItem[] MapBlockTrace(BlockTrace blockTrace);
        Log MapLog(LogEntry logEntry);
    }
}