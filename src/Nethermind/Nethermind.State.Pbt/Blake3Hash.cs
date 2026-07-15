// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.State.Pbt;

/// <summary>The EIP-8297 hash primitives (draft hash function: BLAKE3).</summary>
public static class Blake3Hash
{
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output32) => Blake3.Hasher.Hash(input, output32);

    public static ValueHash256 Hash(ReadOnlySpan<byte> input)
    {
        ValueHash256 result = default;
        Blake3.Hasher.Hash(input, result.BytesAsSpan);
        return result;
    }

    /// <summary>
    /// The EIP-8297 node hash: 32 zero bytes when the input is 64 zero bytes (an empty subtree),
    /// otherwise BLAKE3 of the input.
    /// </summary>
    public static ValueHash256 HashPairOrZero(ReadOnlySpan<byte> left32, ReadOnlySpan<byte> right32)
    {
        if (left32.IsZero() && right32.IsZero()) return default;

        Span<byte> pair = stackalloc byte[64];
        left32.CopyTo(pair);
        right32.CopyTo(pair[32..]);
        return Hash(pair);
    }
}
