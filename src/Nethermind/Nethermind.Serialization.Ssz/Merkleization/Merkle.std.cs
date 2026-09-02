// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Serialization.Ssz.Merkleization;

public static partial class Merkle
{
    private static UInt256 Compute(Span<UInt256> span) =>
        MemoryMarshal.Cast<byte, UInt256>(System.Security.Cryptography.SHA256.HashData(MemoryMarshal.Cast<UInt256, byte>(span)))[0];

    private static UInt256 HashPair(in UInt256 left, in UInt256 right)
    {
        Span<UInt256> concatenation = stackalloc UInt256[2];
        concatenation[0] = left;
        concatenation[1] = right;
        return Compute(concatenation);
    }
}
