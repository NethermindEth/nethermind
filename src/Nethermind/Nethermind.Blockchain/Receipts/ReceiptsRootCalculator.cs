// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Receipts;

public partial class ReceiptsRootCalculator : IReceiptsRootCalculator
{
    public static readonly ReceiptsRootCalculator Instance = new();

    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot)
    {
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts, _decoder);
        if (!spec.ValidateReceipts && receiptsRoot != suggestedRoot)
        {
            Hash256 skipStateAndStatusReceiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts, _skipStateDecoder);
            if (skipStateAndStatusReceiptsRoot == suggestedRoot)
            {
                return skipStateAndStatusReceiptsRoot;
            }
        }

        return receiptsRoot;
    }
}
