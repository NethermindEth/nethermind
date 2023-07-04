// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Receipts
{
    public static class ReceiptsExtensions
    {
        public static TxReceipt ForTransaction(this TxReceipt[] receipts, Keccak txHash)
            => receipts.FirstOrDefault(r => r.TxHash == txHash);

        public static void SetSkipStateAndStatusInRlp(this TxReceipt[] receipts, bool value)
        {
            for (int i = 0; i < receipts.Length; i++)
            {
                receipts[i].SkipStateAndStatusInRlp = value;
            }
        }

        public static Keccak GetReceiptsRoot(this TxReceipt[] txReceipts, IReceiptSpec receiptSpec, Keccak suggestedRoot)
        {
            Keccak SkipStateAndStatusReceiptsRoot()
            {
                txReceipts.SetSkipStateAndStatusInRlp(true);
                try
                {
                    return new ReceiptTrie(receiptSpec, txReceipts).RootHash;
                }
                finally
                {
                    txReceipts.SetSkipStateAndStatusInRlp(false);
                }
            }

            Keccak receiptsRoot = new ReceiptTrie(receiptSpec, txReceipts).RootHash;
            if (!receiptSpec.ValidateReceipts && receiptsRoot != suggestedRoot)
            {
                var skipStateAndStatusReceiptsRoot = SkipStateAndStatusReceiptsRoot();
                if (skipStateAndStatusReceiptsRoot == suggestedRoot)
                {
                    return skipStateAndStatusReceiptsRoot;
                }
            }
            return receiptsRoot;
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
