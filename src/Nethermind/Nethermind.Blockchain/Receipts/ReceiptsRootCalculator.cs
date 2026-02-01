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

    private static readonly IRlpStreamDecoder<TxReceipt> _decoder =
#if ZKVM
        // NativeAOT/ZKVM: avoid relying on decoder registry having a Trie-keyed receipt decoder.
        // `ReceiptMessageDecoder` implements the stream decoder contract and is sufficient for ReceiptTrie encoding.
        new ReceiptMessageDecoder()
#else
        Rlp.GetStreamDecoder<TxReceipt>(RlpDecoderKey.Trie)
#endif
        ?? throw new InvalidOperationException($"Missing RLP stream decoder for {nameof(TxReceipt)} with key '{RlpDecoderKey.Trie}'.");
    private static readonly IRlpStreamDecoder<TxReceipt> _skipStateDecoder = new ReceiptMessageDecoder(skipStateAndStatus: true);

    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot)
    {
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts, _decoder);
        if (!spec.ValidateReceipts && receiptsRoot != suggestedRoot)
        {
            var skipStateAndStatusReceiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts, _skipStateDecoder);
            if (skipStateAndStatusReceiptsRoot == suggestedRoot)
            {
                return skipStateAndStatusReceiptsRoot;
            }
        }

        return receiptsRoot;
    }
}
