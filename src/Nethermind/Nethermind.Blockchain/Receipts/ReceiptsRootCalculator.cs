// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;

namespace Nethermind.Blockchain.Receipts;

public class ReceiptsRootCalculator : IReceiptsRootCalculator
{
    public static readonly ReceiptsRootCalculator Instance = new();
    private static IRlpDecoder<TxReceipt> _decoder => field ??= Rlp.GetDecoderOrThrow<TxReceipt>(RlpDecoderKey.Trie);
    private static ReceiptMessageDecoder _skipStateDecoder => field ??= new(skipStateAndStatus: true);

    /// <inheritdoc/>
    /// <remarks>Reads the receipts with <see cref="NetworkReceipt"/>, which takes the standard receipt forms only.</remarks>
    public Hash256? GetReceiptsRoot(ReadOnlyMemory<byte> blockReceipts, bool withBloom, IReceiptSpec spec) =>
        ReceiptTrie.TryCalculateRoot(spec, blockReceipts.Span, withBloom, out Hash256 root) ? root : null;

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
