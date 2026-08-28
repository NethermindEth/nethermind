// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Stateless.Execution.IO;

/// <summary>
/// Inline 65-byte uncompressed secp256k1 public key (<c>0x04</c> prefix followed by X and Y).
/// </summary>
/// <remarks>
/// Inline storage keeps a decoded <c>SszPublicKey[]</c> as one flat allocation instead of one
/// <c>byte[65]</c> per transaction, which matters inside the zkVM guest where the stateless
/// input is decoded.
/// </remarks>
[InlineArray(PublicKeyLength)]
public struct SszPublicKey
{
    public const int PublicKeyLength = PublicKey.PrefixedLengthInBytes;

    private byte _element0;

    public static SszPublicKey FromSpan(ReadOnlySpan<byte> span)
    {
        if (span.Length != PublicKeyLength)
        {
            throw new InvalidDataException($"{nameof(SszPublicKey)} expects input of length {PublicKeyLength} and received {span.Length}");
        }

        SszPublicKey result = default;
        span.CopyTo(result);
        return result;
    }

    [UnscopedRef]
    public readonly ReadOnlySpan<byte> AsSpan() => this;
}
