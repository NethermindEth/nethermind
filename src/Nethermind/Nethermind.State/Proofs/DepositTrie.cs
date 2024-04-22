// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Deposit"/>.
/// </summary>
public class DepositTrie : PatriciaTrie<Deposit>
{
    private static readonly DepositDecoder _codec = new();

    /// <inheritdoc/>
    /// <param name="Deposits">The Deposits to build the trie of.</param>
    public DepositTrie(Deposit[] Deposits, bool canBuildProof = false)
        : base(Deposits, canBuildProof) => ArgumentNullException.ThrowIfNull(Deposits);

    protected override void Initialize(Deposit[] Deposits)
    {
        var key = 0;

        foreach (var Deposit in Deposits)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(Deposit).Bytes);
        }
    }
}
