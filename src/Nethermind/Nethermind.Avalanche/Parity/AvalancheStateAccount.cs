// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Int256;

namespace Nethermind.Avalanche.Parity;

/// <summary>
/// A state account as serialized by Coreth/libevm on the Avalanche C-Chain.
/// </summary>
/// <remarks>
/// Structurally identical to an Ethereum account (nonce, balance, storage root, code hash) but with a
/// fifth trailing <see cref="IsMultiCoin"/> boolean that is <b>always</b> present in the RLP. See
/// libevm <c>core/types/state_account.libevm_test.go</c> (github.com/ava-labs/libevm) for the canonical
/// byte vectors this type reproduces.
/// <para>
/// The storage root and code hash are kept as raw byte strings rather than <c>Hash256</c> so that the
/// encoding round-trips byte-for-byte even for the non-32-byte placeholder values used in the upstream
/// test vectors. In production these are exactly 32 bytes each (a <c>common.Hash</c>).
/// </para>
/// </remarks>
public readonly struct AvalancheStateAccount(ulong nonce, UInt256 balance, byte[] storageRoot, byte[] codeHash, bool isMultiCoin)
    : IEquatable<AvalancheStateAccount>
{
    public ulong Nonce { get; } = nonce;
    public UInt256 Balance { get; } = balance;

    /// <summary>The raw, RLP-string-encoded storage root bytes (32 bytes in production).</summary>
    public byte[] StorageRoot { get; } = storageRoot ?? [];

    /// <summary>The raw, RLP-string-encoded code hash bytes (32 bytes in production).</summary>
    public byte[] CodeHash { get; } = codeHash ?? [];

    /// <summary>The Coreth-specific trailing flag partitioning multi-coin accounts from normal accounts.</summary>
    public bool IsMultiCoin { get; } = isMultiCoin;

    public bool Equals(AvalancheStateAccount other) =>
        Nonce == other.Nonce &&
        Balance == other.Balance &&
        IsMultiCoin == other.IsMultiCoin &&
        ((ReadOnlySpan<byte>)StorageRoot).SequenceEqual(other.StorageRoot) &&
        ((ReadOnlySpan<byte>)CodeHash).SequenceEqual(other.CodeHash);

    public override bool Equals(object? obj) => obj is AvalancheStateAccount other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Nonce, Balance, IsMultiCoin, StorageRoot.Length, CodeHash.Length);
}
