// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Avalanche.Parity;

/// <summary>
/// Computes the C-Chain block <c>ExtDataHash</c> header field exactly as Coreth does.
/// </summary>
/// <remarks>
/// Coreth blocks carry a slice of atomic-transaction bytes (<c>ExtData</c>) and commit to it in the header
/// via <c>ExtDataHash = keccak256(RLP(extdata))</c>, where <c>extdata</c> is RLP-encoded as a single byte
/// string. Empty (or nil) extdata yields <c>keccak256(RLP("")) = keccak256(0x80)</c>, which coincides with
/// the keccak of an empty RLP byte string. Source: Coreth <c>CalcExtDataHash</c> /
/// <c>EmptyExtDataHash = rlpHash([]byte(nil))</c> (github.com/ava-labs/coreth, <c>core/types</c>).
/// </remarks>
public static class AvalancheExtData
{
    /// <summary><c>keccak256(RLP(""))</c> — the ExtDataHash of empty/nil extdata.</summary>
    public static readonly ValueHash256 EmptyExtDataHash = ValueKeccak.Compute(Rlp.Encode(ReadOnlySpan<byte>.Empty).Bytes);

    /// <summary>
    /// Computes <c>keccak256(RLP(extdata))</c>. Returns <see cref="EmptyExtDataHash"/> for empty input.
    /// </summary>
    /// <param name="extData">The raw atomic-transaction bytes; empty for blocks without atomic data.</param>
    public static ValueHash256 CalcExtDataHash(ReadOnlySpan<byte> extData)
    {
        if (extData.IsEmpty)
        {
            return EmptyExtDataHash;
        }

        return ValueKeccak.Compute(Rlp.Encode(extData).Bytes);
    }
}
