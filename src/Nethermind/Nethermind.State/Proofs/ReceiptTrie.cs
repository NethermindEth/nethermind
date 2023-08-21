// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="TxReceipt"/>.
/// </summary>
public class ReceiptTrie : PatriciaTrie<TxReceipt>
{
    private static readonly ReceiptMessageDecoder _decoder = new();

    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, IEnumerable<TxReceipt> receipts, bool canBuildProof = false, IBufferPool? bufferPool = null)
        : base(null, canBuildProof, bufferPool: bufferPool)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(receipts);

        if (receipts.Any())
        {
            Initialize(receipts, spec);
            UpdateRootHash();
        }
    }

    private void Initialize(IEnumerable<TxReceipt> receipts, IReceiptSpec spec)
    {
        RlpBehaviors behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
                                | RlpBehaviors.SkipTypedWrapping;
        int key = 0;

        foreach (TxReceipt? receipt in receipts)
        {
            int size = _decoder.GetLength(receipt, behavior);
            CappedArray<byte> buffer = _bufferPool.SafeRentBuffer(size);
            _decoder.Encode(buffer.AsRlpStream(), receipt, behavior);

            int theKey = key++;
            CappedArray<byte> keyBuffer = _bufferPool.SafeRentBuffer(Rlp.LengthOf(theKey));
            keyBuffer.AsRlpStream().Encode(theKey);
            Set(keyBuffer.AsSpan(), buffer);
        }
    }

    protected override void Initialize(IEnumerable<TxReceipt> list) => throw new NotSupportedException();

    public static Keccak CalculateRoot(IReceiptSpec receiptSpec, IList<TxReceipt> txReceipts)
    {
        TrackedPooledBufferTrieStore bufferPool = new(txReceipts.Count * 4);
        Keccak receiptsRoot = new ReceiptTrie(receiptSpec, txReceipts, bufferPool: bufferPool).RootHash;
        bufferPool.ReturnAll();
        return receiptsRoot;
    }
}
