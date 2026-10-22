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
    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256 into <paramref name="parent"/>, which may alias either chunk.</summary>
    /// <remarks>Hashes into the result: a <c>byte[32]</c> per merkle node cost the guest ~140 steps
    /// each, several times the hash itself.</remarks>
    [SkipLocalsInit]
    private static void HashPair(in UInt256 left, in UInt256 right, out UInt256 parent)
    {
        Span<UInt256> concatenation = stackalloc UInt256[2];
        concatenation[0] = left;
        concatenation[1] = right;

        Unsafe.SkipInit(out parent);
        SHA256.HashData(
            MemoryMarshal.AsBytes(concatenation),
            MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref parent, 1)));
    }

    /// <summary>Writes the parent of two nodes at <paramref name="level"/> into <paramref name="parent"/>, which may alias either child.</summary>
    private static void HashNodes(in UInt256 left, in UInt256 right, int level, out UInt256 parent) =>
        parent = HashConcatenation(left, right, level);
}
