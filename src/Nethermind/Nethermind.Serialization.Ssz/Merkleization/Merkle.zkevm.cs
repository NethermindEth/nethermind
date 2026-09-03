// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Int256;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    /// <summary>Hashes the 64-byte concatenation of two chunks with SHA-256.</summary>
    /// <remarks>Hashes into the result: the <c>byte[32]</c> the wrapper allocated was one allocation
    /// per merkle node, ~140 guest steps each. See <c>Merkle.std.cs</c> for the host form.</remarks>
    [SkipLocalsInit]
    private static UInt256 HashPair(in UInt256 left, in UInt256 right)
    {
        Span<UInt256> concatenation = stackalloc UInt256[2];
        concatenation[0] = left;
        concatenation[1] = right;

        Unsafe.SkipInit(out UInt256 result);
        Accelerators.Sha256(
            MemoryMarshal.AsBytes(concatenation),
            MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref result, 1)));
        return result;
    }
}
