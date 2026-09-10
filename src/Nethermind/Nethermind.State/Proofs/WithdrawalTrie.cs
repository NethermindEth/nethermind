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

    /// <summary>Computes the withdrawal root defined by EIP-4895.</summary>
    /// <remarks>The trie keys are list positions; <see cref="Withdrawal.Index"/> is part of each value.</remarks>
    public static Hash256 CalculateRoot(ReadOnlySpan<Withdrawal> withdrawals) =>
        new IndexedTrieRoot.Calculator<Withdrawal, WithdrawalEncoder>(withdrawals, default).Calculate(canBeParallel: false);

    protected override void Initialize(ReadOnlySpan<Withdrawal> withdrawals)
    {
        int key = 0;

        foreach (Withdrawal withdrawal in withdrawals)
        {
            Set(Rlp.Encode(key++).Bytes, _codec.EncodeAsBytes(withdrawal));
        }
    }

    private readonly struct WithdrawalEncoder : IndexedTrieRoot.IValueEncoder<Withdrawal>
    {
        public ReadOnlySpan<byte> GetEncodedValue(Withdrawal item) => default;
        public int GetLength(Withdrawal item) => _codec.GetLength(item, RlpBehaviors.None);
        public void Encode<TWriter>(ref TWriter writer, Withdrawal item) where TWriter : struct, IRlpWriteBackend, allows ref struct =>
            _codec.Encode(ref writer, item);
    }
}
