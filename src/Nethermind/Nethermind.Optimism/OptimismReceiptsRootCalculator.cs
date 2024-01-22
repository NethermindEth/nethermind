// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Optimism;

public class OptimismReceiptsRootCalculator : IReceiptsRootCalculator
{
    public static readonly OptimismReceiptsRootCalculator Instance = new();

    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot)
    {
        Hash256 SkipStateAndStatusReceiptsRoot()
        {
            receipts.SetSkipStateAndStatusInRlp(true);
            try
            {
                return ReceiptTrie<OptimismTxReceipt>.CalculateRoot(spec, receipts.Cast<OptimismTxReceipt>().ToArray(), OptimismReceiptDecoder.Instance);
            }
            finally
            {
                receipts.SetSkipStateAndStatusInRlp(false);
            }
        }

        Hash256 receiptsRoot = ReceiptTrie<OptimismTxReceipt>.CalculateRoot(spec, receipts.Cast<OptimismTxReceipt>().ToArray(), OptimismReceiptDecoder.Instance);
        if (!spec.ValidateReceipts && receiptsRoot != suggestedRoot)
        {
            var skipStateAndStatusReceiptsRoot = SkipStateAndStatusReceiptsRoot();
            if (skipStateAndStatusReceiptsRoot == suggestedRoot)
            {
                return skipStateAndStatusReceiptsRoot;
            }
        }

        return receiptsRoot;
    }
}
