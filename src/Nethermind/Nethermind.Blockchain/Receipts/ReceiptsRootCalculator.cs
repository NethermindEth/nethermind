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

    /// <summary>Computes the receipts root of a block's receipts as a receipts message carries them.</summary>
    /// <remarks>
    /// The root is the one <see cref="GetReceiptsRoot"/> computes for the decoded receipts when they are in canonical
    /// form. A root that differs from the expected one is to be checked again on the decoded receipts, which
    /// <see cref="GetReceiptsRoot"/> may normalize or compute without their state and status.
    /// </remarks>
    /// <param name="blockReceipts">The encoded sequence of the block's receipts.</param>
    /// <param name="withBloom">Whether the receipts have blooms: the eth/63 to eth/68 form.</param>
    /// <returns>The root, or <see langword="null"/> when the receipts must be decoded for it.</returns>
    public Hash256? TryGetReceiptsRoot(ReadOnlySpan<byte> blockReceipts, bool withBloom, IReceiptSpec spec) =>
        _decoder.GetType() == typeof(ReceiptMessageDecoder) && ReceiptTrie.TryCalculateRoot(spec, blockReceipts, withBloom, out Hash256 root)
            ? root
            : null;

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
