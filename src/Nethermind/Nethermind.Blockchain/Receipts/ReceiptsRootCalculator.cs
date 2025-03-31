// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Receipts;

public class ReceiptsRootCalculator : IReceiptsRootCalculator
{
    public static readonly ReceiptsRootCalculator Instance = new();

    private static readonly IRlpStreamDecoder<TxReceipt> _decoder = Rlp.GetStreamDecoder<TxReceipt>(RlpDecoderKey.Trie);

    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot)
    {
        Hash256 SkipStateAndStatusReceiptsRoot()
        {
            receipts.SetSkipStateAndStatusInRlp(true);
            try
            {
                return ReceiptTrie<TxReceipt>.CalculateRoot(spec, receipts, _decoder);
            }
            finally
            {
                receipts.SetSkipStateAndStatusInRlp(false);
            }
        }

        Hash256 receiptsRoot = ReceiptTrie<TxReceipt>.CalculateRoot(spec, receipts, _decoder);
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
