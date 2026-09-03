// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256.</summary>
    /// <remarks>Hashes into the result rather than through <see cref="SHA256.HashData(ReadOnlySpan{byte})"/>,
    /// whose <c>byte[32]</c> would be one gen0 allocation per merkle node.</remarks>
    [SkipLocalsInit]
    private static UInt256 HashPair(in UInt256 left, in UInt256 right)
    {
        Span<UInt256> concatenation = stackalloc UInt256[2];
        concatenation[0] = left;
        concatenation[1] = right;

        Unsafe.SkipInit(out UInt256 result);
        SHA256.HashData(
            MemoryMarshal.AsBytes(concatenation),
            MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1)));
        return result;
    }
}
