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
// 

using System.Linq;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Evm.Tracing
{
    public class TxTraceFilter
    {
        private readonly Address[]? _fromAddresses;
        private readonly Address[]? _toAddresses;
        private int _after;
        private int? _count;
        private readonly ILogger _logger;
        
        public TxTraceFilter(
            Address[]? fromAddresses,
            Address[]? toAddresses,
            int after,
            int? count,
            ILogManager logManager)
        {
            _fromAddresses = fromAddresses;
            _toAddresses = toAddresses;
            _after = after;
            _count = count;
            _logger = logManager.GetClassLogger();
        }

        public bool ShouldTraceTx(Transaction? tx)
        {
            _logger.Warn($"Tracing transaction {tx}, from: {tx?.SenderAddress}, to: {tx?.To}, fromAddresses: {_fromAddresses}, toAddresses {_toAddresses}, after {_after}, count {_count}");
            if (_logger.IsTrace) _logger.Trace($"Tracing transaction {tx}, from: {tx?.SenderAddress}, to: {tx?.To}, fromAddresses: {_fromAddresses}, toAddresses {_toAddresses}, after {_after}, count {_count}");
            if (tx == null ||
                !TxMatchesAddresses(tx) ||
                (_count <= 0))
            {
                return false;
            }

            if (_after > 0)
            {
                --_after;
                return false;
            }
            
            --_count;
            return true;
        }

        public bool ShouldContinue()
        {
            return _count == null ||  _count > 0;
        }

        public bool ShouldTraceBlock(Block? block)
        {
            if (block == null)
                return false;
            
            int txCount = CountMatchingTransactions(block);
            if (_logger.IsTrace)
                _logger.Trace(
                    $"Checking if we should trace block {block}, matching tx count: {txCount}, after: {_after}");
            if (_after >= txCount)
            {
                // we can skip the block if it don't achieve after
                _after -= txCount;
                return false;
            }

            return true;
        }

        private int CountMatchingTransactions(Block block)
        {
            if (_fromAddresses == null && _toAddresses == null)
                return block.Transactions.Length;

            int counter = 0;
            for (int index = 0; index < block.Transactions.Length; index++)
            {
                Transaction tx = block.Transactions[index];
                if (TxMatchesAddresses(tx))
                    ++counter;
            }

            return counter;
        }

        private bool TxMatchesAddresses(Transaction tx)
        {
            return (_fromAddresses == null || _fromAddresses.Contains(tx.SenderAddress)) &&
                (_toAddresses == null || _toAddresses.Contains(tx.To));
        }
    }
}
