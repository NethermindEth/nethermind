// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using System;

namespace Nethermind.Core;
public class AuthorizationTuple
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

    public AuthorizationTuple(
        ulong chainId,
        Address codeAddress,
        UInt256? nonce,
        Signature sig, Address? authority = null)
    {
        ChainId = chainId;
        CodeAddress = codeAddress ?? throw new System.ArgumentNullException(nameof(codeAddress));
        Nonce = nonce;
        AuthoritySignature = sig ?? throw new System.ArgumentNullException(nameof(sig));
        Authority = authority;
    }

    public ulong ChainId { get; }
    public Address CodeAddress { get; }
    public UInt256? Nonce { get; }
    public Signature AuthoritySignature { get; }
    /// <summary>
    /// <see cref="Authority"/> may be recovered at a later point.
    /// </summary>
    public Address? Authority { get; set; }
}
