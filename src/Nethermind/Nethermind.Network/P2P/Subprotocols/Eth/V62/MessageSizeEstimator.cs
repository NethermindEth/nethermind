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

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public static class MessageSizeEstimator
    {
        public static ulong EstimateSize(BlockHeader blockHeader)
        {
            if (blockHeader == null)
            {
                return 0;
            }

            return 512; // rough knowledge about the size of a header
        }

        public static ulong EstimateSize(Transaction tx)
        {
            if (tx == null)
            {
                return 0;
            }

            return 100UL + (ulong) (tx.Data?.Length ?? 0);
        }

        public static ulong EstimateSize(Block block)
        {
            if (block == null)
            {
                return 0;
            }

            ulong estimate = EstimateSize(block.Header);
            var transactions = block.Transactions;
            for (int i = 0; i < transactions.Length; i++)
            {
                estimate += EstimateSize(transactions[i]);
            }

            return estimate;
        }

        public static ulong EstimateSize(TxReceipt receipt)
        {
            ulong estimate = Bloom.ByteLength; // receipt is mostly Bloom + Logs
            for (int i = 0; i < receipt.Logs.Length; i++)
            {
                estimate += (ulong) receipt.Logs[i].Data.Length + (ulong) receipt.Logs[i].Topics.Length * Keccak.Size;
            }

            return estimate;
        }
    }
}
