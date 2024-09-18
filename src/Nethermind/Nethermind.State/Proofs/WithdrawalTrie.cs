// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Withdrawal"/>.
/// </summary>
public class WithdrawalTrie : PatriciaTrie<Withdrawal>
{
    /// <inheritdoc/>
    /// <param name="withdrawals">The withdrawals to build the trie of.</param>
    /// <param name="bufferPool"></param>
    /// <param name="canBuildProof"></param>
    public WithdrawalTrie(Withdrawal[]? withdrawals, ICappedArrayPool bufferPool, bool canBuildProof = false)
        : base(withdrawals, new WithdrawalDecoder(), bufferPool, canBuildProof) => ArgumentNullException.ThrowIfNull(withdrawals);

    public static Hash256 CalculateRoot(Withdrawal[] withdrawals)
    {
        using TrackingCappedArrayPool cappedArrayPool = new(withdrawals.Length * 4);
        return new WithdrawalTrie(withdrawals, bufferPool: cappedArrayPool).RootHash;
    }

    public static byte[][] CalculateProof(Withdrawal[] withdrawals, int index)
    {
        using TrackingCappedArrayPool cappedArray = new(withdrawals.Length * 4);
        return new WithdrawalTrie(withdrawals, bufferPool: cappedArray, canBuildProof: true).BuildProof(index);
    }

}
