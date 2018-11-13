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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm.Tracing
{
    public class BlockTraceListener : ITraceListener
    {
        private Keccak _blockHash;
        private int _currentIndex;

        public BlockTraceListener(Block block)
        {
            _blockHash = block.Hash;
            BlockTrace = new BlockTrace(new TransactionTrace[block.Transactions.Length]);
        }

        public BlockTrace BlockTrace { get; set; }

        public bool ShouldTrace(Keccak txHash)
        {
            return true;
        }

        public void RecordTrace(Keccak txHash, TransactionTrace trace)
        {
            if (_currentIndex > BlockTrace.TxTraces.Length - 1) throw new InvalidOperationException($"Unexpected trace for tx {txHash} beyond the number of transactions in block {_blockHash}");
            BlockTrace.TxTraces[_currentIndex++] = trace;
        }
    }
}