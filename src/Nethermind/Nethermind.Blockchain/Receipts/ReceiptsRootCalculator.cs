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

    private readonly IRlpStreamEncoder<TxReceipt> _decoder;
    private readonly IRlpStreamEncoder<TxReceipt> _skipStateDecoder = new ReceiptMessageDecoder(skipStateAndStatus: true);

    public ReceiptsRootCalculator() : this(Rlp.DefaultRegistry) { }

    public ReceiptsRootCalculator(IRlpDecoderRegistry registry) =>
        _decoder = registry.GetStreamEncoder<TxReceipt>(RlpDecoderKey.Trie)!;

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
