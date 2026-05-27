// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;

public class AuthorizationTuple(
    UInt256 chainId,
    Address codeAddress,
    ulong nonce,
    Signature sig,
    Address? authority = null) : IEquatable<AuthorizationTuple>
{
    public AuthorizationTuple(
        UInt256 chainId,
        Address codeAddress,
        ulong nonce,
        byte yParity,
        UInt256 r,
        UInt256 s,
        Address? authority = null) : this(chainId, codeAddress, nonce, new Signature(r, s, (ulong)yParity + Signature.VOffset), authority)
    { }

    public UInt256 ChainId { get; } = chainId;
    public Address CodeAddress { get; protected set; } = codeAddress;
    public ulong Nonce { get; } = nonce;
    public Signature AuthoritySignature { get; protected set; } = sig;

    /// <summary>
    /// <see cref="Authority"/> may be recovered at a later point.
    /// </summary>
    public Address? Authority { get; set; } = authority;

    public override bool Equals(object? obj) => obj is AuthorizationTuple other && Equals(other);

    public bool Equals(AuthorizationTuple? other) =>
        ReferenceEquals(this, other) ||
        other is not null &&
        ChainId == other.ChainId &&
        CodeAddress == other.CodeAddress &&
        Nonce == other.Nonce &&
        Equals(AuthoritySignature, other.AuthoritySignature);

    public override int GetHashCode() => HashCode.Combine(ChainId, CodeAddress, Nonce, AuthoritySignature);

    public override string ToString() => $"Delegation authorization from {Authority} to {CodeAddress} on chain {ChainId} with Nonce {Nonce}";
}
