// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Withdrawal"/>.
/// </summary>
public class WithdrawalTrie : PatriciaTrie<Withdrawal>
{
    private static readonly WithdrawalDecoder _codec = new();

    /// <inheritdoc/>
    /// <param name="withdrawals">The withdrawals to build the trie of.</param>
    public WithdrawalTrie(IEnumerable<Withdrawal> withdrawals, bool canBuildProof = false)
        : base(withdrawals, canBuildProof) => ArgumentNullException.ThrowIfNull(withdrawals);

    protected override void Initialize(IEnumerable<Withdrawal> withdrawals)
    {
        var key = 0;

        foreach (var withdrawal in withdrawals)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(withdrawal).Bytes);
        }
    }
}
