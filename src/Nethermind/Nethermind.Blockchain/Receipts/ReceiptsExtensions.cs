// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Blockchain.Receipts
{
    public static class ReceiptsExtensions
    {
        public static TxReceipt ForTransaction(this TxReceipt[] receipts, Hash256 txHash)
            => receipts.FirstOrDefault(r => r.TxHash == txHash);


        public static int GetBlockLogFirstIndex(this TxReceipt[] receipts, int receiptIndex)
        {
            int sum = 0;
            for (int i = 0; i < receipts.Length; ++i)
            {
                TxReceipt receipt = receipts[i];
                if (receipt.Index < receiptIndex)
                {
                    if (receipt.Logs is not null)
                    {
                        sum += receipt.Logs.Length;
                    }
                }
            }
            return sum;
        }

        /// <summary>Replaces each receipt's running gas total with the transaction's own gas used.</summary>
        /// <remarks>EIP-8116: post-fork receipts commit per-transaction gas in the <c>cumulativeGasUsed</c> field.</remarks>
        public static void SetEip8116GasUsed(this TxReceipt[] receipts)
        {
            foreach (TxReceipt receipt in receipts)
            {
                receipt.GasUsedTotal = receipt.GasUsed;
            }
        }

        /// <summary>Sets the senders the stored receipts of <paramref name="block"/> record on its transactions that have
        /// none, so that they are not recovered from their signatures again.</summary>
        /// <remarks>Only for a block read from the store: its receipts are found by its hash and matched to its
        /// transactions by position. A receipt that records no sender, a frame transaction, and every transaction of a
        /// block whose receipts are missing or do not count one per transaction are left to signature recovery.</remarks>
        public static void SetSendersFromStoredReceipts(this IReceiptFinder receiptFinder, Block block)
        {
            Transaction[] transactions = block.Transactions;
            if (block.Hash is not { } blockHash || !LacksSender(transactions)
                || !receiptFinder.TryGetReceiptsIterator(block.Number, blockHash, out ReceiptsIterator iterator))
            {
                return;
            }

            using ReceiptsIterator receipts = iterator;
            if (receipts.CountRemaining() != transactions.Length) return;

            for (int i = 0; i < transactions.Length && receipts.TryGetNext(out TxReceiptStructRef receipt); i++)
            {
                Transaction transaction = transactions[i];
                // An absent sender decodes to the shared zero address, which a stored one never aliases.
                if (transaction.SenderAddress is null && transaction.IsSigned && !transaction.SupportsFrames && receipt.Sender.Bytes != Address.Zero.Bytes)
                {
                    transaction.SenderAddress = receipt.Sender.ToAddress();
                }
            }
        }

        private static bool LacksSender(Transaction[] transactions)
        {
            foreach (Transaction transaction in transactions)
            {
                if (transaction.SenderAddress is null) return true;
            }

            return false;
        }

        /// <summary>Sets every receipt's bloom to the zero-length <see cref="Bloom.ZeroLength"/> that EIP-7668 requires.</summary>
        public static void SetZeroLengthBlooms(this TxReceipt[] receipts)
        {
            foreach (TxReceipt receipt in receipts)
            {
                receipt.Bloom = Bloom.ZeroLength;
            }
        }
    }
}
