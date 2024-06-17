// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;
public class AuthorizationTuple
{
    public AuthorizationTuple(
        ulong chainId,
        Address codeAddress,
        UInt256? nonce,
        ulong yParity,
        byte[] r,
        byte[] s) : this(chainId, codeAddress, nonce, new Signature(r, s, yParity + Signature.VOffset))
    { }

    public AuthorizationTuple(
        ulong chainId,
        Address codeAddress,
        UInt256? nonce,
        Signature sig)
    {
        ChainId = chainId;
        CodeAddress = codeAddress ?? throw new System.ArgumentNullException(nameof(codeAddress));
        Nonce = nonce;
        AuthoritySignature = sig ?? throw new System.ArgumentNullException(nameof(sig));
    }

    public ulong ChainId { get; }
    public Address CodeAddress { get; }
    public UInt256? Nonce { get; }
    public Signature AuthoritySignature { get; }
}
