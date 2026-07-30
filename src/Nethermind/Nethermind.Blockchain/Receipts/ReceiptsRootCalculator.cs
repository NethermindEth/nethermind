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
    private static readonly IRlpDecoder<TxReceipt> _decoder = Rlp.GetDecoder<TxReceipt>(RlpDecoderKey.Trie)!;
    private static readonly ReceiptMessageDecoder _skipStateDecoder = new(skipStateAndStatus: true);

    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot) =>
        GetReceiptsRoot(receipts, spec, suggestedRoot, allowParallel: true);

    /// <summary>
    /// <inheritdoc cref="GetReceiptsRoot(TxReceipt[], IReceiptSpec, Hash256?)"/>
    /// </summary>
    /// <remarks>
    /// <paramref name="allowParallel"/> lets callers that compute the root concurrently with other
    /// parallel work (e.g. the block-commit phase) keep the trie hashing off the shared cores.
    /// </remarks>
    public Hash256 GetReceiptsRoot(TxReceipt[] receipts, IReceiptSpec spec, Hash256? suggestedRoot, bool allowParallel)
    {
        Hash256 receiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts, _decoder, allowParallel);
        if (!spec.ValidateReceipts && receiptsRoot != suggestedRoot)
        {
            Hash256 skipStateAndStatusReceiptsRoot = ReceiptTrie.CalculateRoot(spec, receipts, _skipStateDecoder, allowParallel);
            if (skipStateAndStatusReceiptsRoot == suggestedRoot)
            {
                return skipStateAndStatusReceiptsRoot;
            }
        }

        return receiptsRoot;
    }
}
