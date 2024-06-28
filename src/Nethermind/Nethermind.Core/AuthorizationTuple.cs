// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using System;
using System.Diagnostics.CodeAnalysis;

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

    /// <summary>
    /// Determines if this <see cref="AuthorizationTuple"/> is wellformed according to spec.
    /// </summary>
    /// <param name="accountStateProvider"></param>
    /// <param name="chainId"></param>
    /// <param name="error"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException">When <see cref="Authority"/> has not been set.</exception>
    public bool IsWellformed(IAccountStateProvider accountStateProvider, ulong chainId, [NotNullWhen(false)] out string? error)
    {
        if (Authority is null)
            throw new InvalidOperationException($"Cannot determine correctness when {nameof(Authority)} is null.");
        if (ChainId != 0 && chainId != ChainId)
        {
            error = $"Chain id ({ChainId}) does not match.";
            return false;
        }
        if (accountStateProvider.HasCode(Authority))
        {
            error = $"Authority ({Authority}) has code deployed.";
            return false;
        }
        if (Nonce is not null && accountStateProvider.GetNonce(Authority) != Nonce)
        {
            error = $"Skipping tuple in authorization_list because authority ({Authority}) nonce ({Nonce}) does not match.";
            return false;
        }
        error = null;
        return true;
    }
}
