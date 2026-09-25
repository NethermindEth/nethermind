// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>The EIP-8297 hash primitives (draft hash function: BLAKE3).</summary>
public static class Blake3Hash
{
    public static ValueHash256 Hash(ReadOnlySpan<byte> input)
    {
        ValueHash256 result = default;
        Blake3Managed.Hash(input, result.BytesAsSpan);
        return result;
    }

    /// <summary>Hashes two independent inputs in lock-step; see <see cref="Blake3Managed.HashTwo"/>.</summary>
    public static void HashTwo(ReadOnlySpan<byte> inputA, ReadOnlySpan<byte> inputB, out ValueHash256 hashA, out ValueHash256 hashB)
    {
        hashA = default;
        hashB = default;
        Blake3Managed.HashTwo(inputA, hashA.BytesAsSpan, inputB, hashB.BytesAsSpan);
    }
}
