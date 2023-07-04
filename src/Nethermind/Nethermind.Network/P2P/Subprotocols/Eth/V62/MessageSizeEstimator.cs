// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62
{
    public static class MessageSizeEstimator
    {
        public static ulong EstimateSize(BlockHeader blockHeader)
        {
            if (blockHeader is null)
            {
                return 0;
            }

            return 512; // rough knowledge about the size of a header
        }

        public static ulong EstimateSize(Transaction tx)
        {
            if (tx is null)
            {
                return 0;
            }

            return 100UL + (ulong)(tx.Data?.Length ?? 0);
        }

        public static ulong EstimateSize(Block block)
        {
            if (block is null)
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
                estimate += (ulong)receipt.Logs[i].Data.Length + (ulong)receipt.Logs[i].Topics.Length * Keccak.Size;
            }

            return estimate;
        }
    }
}
