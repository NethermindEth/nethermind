// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;
using Nethermind.Trie;

namespace Nethermind.Blockchain.Receipts
{
    public static class ReceiptsExtensions
    {
        public static TxReceipt ForTransaction(this TxReceipt[] receipts, Hash256 txHash)
            => receipts.FirstOrDefault(r => r.TxHash == txHash);

        public static void SetSkipStateAndStatusInRlp(this TxReceipt[] receipts, bool value)
        {
            for (int i = 0; i < receipts.Length; i++)
            {
                receipts[i].SkipStateAndStatusInRlp = value;
            }
        }

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
    }
}
