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
