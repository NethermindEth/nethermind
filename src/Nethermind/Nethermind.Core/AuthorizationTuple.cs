// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;
public class AuthorizationTuple(
    UInt256 chainId,
    Address codeAddress,
    ulong nonce,
    Signature sig,
    Address? authority = null)
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

    public override string ToString() => $"Delegation authorization from {Authority} to {CodeAddress} on chain {ChainId} with Nonce {Nonce}";
}
