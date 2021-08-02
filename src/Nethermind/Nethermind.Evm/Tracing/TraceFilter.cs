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

namespace Nethermind.Evm.Tracing
{
    public class TxTraceFilter
    {
        public TxTraceFilter(
            Address[]? fromAddresses,
            Address[]? toAddresses,
            int after,
            int? count)
        {
            FromAddresses = fromAddresses;
            ToAddresses = toAddresses;
            After = after;
            Count = count;
        }
        public Address[]? FromAddresses { get; }
        
        public Address[]? ToAddresses { get; }
        
        public int After { get; private set; } 
        
        public int? Count { get; private set; }

        public bool ShouldTraceTx(Transaction? tx)
        {
            if (tx == null ||
                TxMatchesAddresses(tx) ||
                (Count <= 0))
            {
                return false;
            }

            if (After > 0)
            {
                --After;
                return false;
            }
            
            --Count;
            return true;
        }

        public bool ShouldContinue()
        {
            return Count == null ||  Count > 0;
        }

        public bool ShouldTraceBlock(Block? block)
        {
            if (block == null)
                return false;
            
            int txCount = CountMatchingTransactions(block);
            if (After >= txCount)
            {
                // we can skip the block if it don't achieve after
                After -= txCount;
                return false;
            }

            return true;
        }

        private int CountMatchingTransactions(Block block)
        {
            if (FromAddresses == null && ToAddresses == null)
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
            return FromAddresses != null && !FromAddresses.Contains(tx.SenderAddress) ||
                ToAddresses != null && !ToAddresses.Contains(tx.To))
        }
    }
}
