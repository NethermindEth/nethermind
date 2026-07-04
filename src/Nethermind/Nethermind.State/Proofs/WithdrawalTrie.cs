// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Proofs;

/// <summary>
/// Represents a Patricia trie built of a collection of <see cref="Withdrawal"/>.
/// </summary>
public sealed class WithdrawalTrie : PatriciaTrie<Withdrawal>
{
    private static readonly WithdrawalDecoder _codec = new();

    /// <inheritdoc/>
    /// <param name="withdrawals">The withdrawals to build the trie of.</param>
    public WithdrawalTrie(ReadOnlySpan<Withdrawal> withdrawals, bool canBuildProof = false)
        : base(withdrawals, canBuildProof, canBeParallel: false) { }

    public static Hash256? CalculateRoot(ReadOnlySpan<Withdrawal> withdrawals) =>
        new WithdrawalTrie(withdrawals).RootHash;

    /// <summary>
    /// Calculates the root from a block body's raw RLP withdrawals sequence (including its list prefix),
    /// without decoding the withdrawals; trie values are the raw items verbatim.
    /// </summary>
    /// <exception cref="RlpException">The sequence is malformed or exceeds <paramref name="countLimit"/>.</exception>
    public static Hash256 CalculateRoot(ReadOnlySpan<byte> withdrawalsSequence, RlpLimit countLimit)
    {
        RlpReader reader = new(withdrawalsSequence);
        int end = reader.ReadSequenceLength() + reader.Position;
        int count = reader.PeekNumberOfItemsRemaining(end, countLimit.Limit + 1);
        reader.GuardLimit(count, countLimit);

        WithdrawalTrie trie = new(ReadOnlySpan<Withdrawal>.Empty);
        for (int key = 0; key < count; key++)
        {
            trie.Set(Rlp.Encode(key).Bytes, reader.Read(reader.PeekNextRlpLength()).ToArray());
        }

        reader.Check(end);
        trie.UpdateRootHash(canBeParallel: false);
        return trie.RootHash;
    }

    protected override void Initialize(ReadOnlySpan<Withdrawal> withdrawals)
    {
        int key = 0;

        foreach (Withdrawal withdrawal in withdrawals)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.Encode(withdrawal).Bytes);
        }
    }
}
