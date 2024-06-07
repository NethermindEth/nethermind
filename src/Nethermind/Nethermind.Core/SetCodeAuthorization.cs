// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core;
public class SetCodeAuthorization
{
    public SetCodeAuthorization(
        ulong chainId,
        Address codeAddress,
        UInt256 nonce,
        Signature sig) : this(chainId, codeAddress, nonce, sig.V, sig.R, sig.S)
    {}

    public SetCodeAuthorization(
        ulong chainId,
        Address codeAddress,
        UInt256 nonce,
        ulong yParity,
        byte[] r,
        byte[] s)
    {
        ChainId = chainId;
        CodeAddress = codeAddress;
        Nonce = nonce;
        YParity = yParity;
        R = r;
        S = s;
    }
    public ulong? ChainId { get; }
    public Address? CodeAddress { get; }
    public UInt256? Nonce { get; }
    public ulong YParity { get; }
    public byte[] R { get; }
    public byte[] S { get; }

    public Hash256 Digest()
    {
        
    }
}
