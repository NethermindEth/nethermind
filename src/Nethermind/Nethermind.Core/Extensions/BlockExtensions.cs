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

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Extensions
{
    public static class BlockExtensions
    {
        public static bool TryFindLog(this Block block, TxReceipt[] receipts, LogEntry matchEntry, IEqualityComparer<LogEntry> comparer, out LogEntry foundEntry)
            => block.Header.TryFindLog(receipts, matchEntry, comparer, out foundEntry);

        private static bool TryFindLog(this BlockHeader blockHeader, TxReceipt[] receipts, LogEntry matchEntry, IEqualityComparer<LogEntry> comparer, out LogEntry foundEntry)
        {
            if (blockHeader.Bloom.Matches(matchEntry))
            {
                // iterating backwards, we are interested only in the last one
                for (int i = receipts.Length - 1; i >= 0; i--)
                {
                    var receipt = receipts[i];
                    if (receipt.Bloom.Matches(matchEntry))
                    {
                        for (int j = receipt.Logs.Length - 1; j >= 0; j--)
                        {
                            var receiptLog = receipt.Logs[j];
                            if (comparer.Equals(matchEntry, receiptLog))
                            {
                                foundEntry = receiptLog;
                                return true;                                
                            }
                        }
                    }
                }
            }

            foundEntry = null;
            return false;
        }
    }
}