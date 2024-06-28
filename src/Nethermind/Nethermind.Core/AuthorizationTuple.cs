// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using System;

namespace Nethermind.Core;
public class AuthorizationTuple(
    ulong chainId,
    Address codeAddress,
    UInt256? nonce,
    Signature sig,
    Address? authority = null)
{
    public AuthorizationTuple(
        ulong chainId,
        Address codeAddress,
        UInt256? nonce,
        ulong yParity,
        byte[] r,
        byte[] s,
        Address? authority = null) : this(chainId, codeAddress, nonce, new Signature(r, s, yParity + Signature.VOffset), authority)
    { }

    public ulong ChainId { get; } = chainId;
    public Address CodeAddress { get; } = codeAddress ?? throw new ArgumentNullException(nameof(codeAddress));
    public UInt256? Nonce { get; } = nonce;
    public Signature AuthoritySignature { get; } = sig ?? throw new ArgumentNullException(nameof(sig));

    /// <summary>
    /// <see cref="Authority"/> may be recovered at a later point.
    /// </summary>
    public Address? Authority { get; set; } = authority;
}
