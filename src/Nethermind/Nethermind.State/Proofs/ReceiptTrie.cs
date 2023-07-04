// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="TxReceipt"/>.
/// </summary>
public class ReceiptTrie : PatriciaTrie<TxReceipt>
{
    private static readonly ReceiptMessageDecoder _decoder = new();

    /// <inheritdoc/>
    /// <param name="receipts">The transaction receipts to build the trie of.</param>
    public ReceiptTrie(IReceiptSpec spec, IEnumerable<TxReceipt> receipts, bool canBuildProof = false)
        : base(null, canBuildProof)
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
        var behavior = (spec.IsEip658Enabled ? RlpBehaviors.Eip658Receipts : RlpBehaviors.None)
            | RlpBehaviors.SkipTypedWrapping;
        var key = 0;

        foreach (var receipt in receipts)
        {
            Set(Rlp.Encode(key++).Bytes, _decoder.EncodeNew(receipt, behavior));
        }
    }

    protected override void Initialize(IEnumerable<TxReceipt> list) => throw new NotSupportedException();
}
