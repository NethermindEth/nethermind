// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Pbt;

/// <summary>The EIP-8297 hash primitives (draft hash function: BLAKE3).</summary>
public static class Blake3Hash
{
    public static void Hash(ReadOnlySpan<byte> input, Span<byte> output32) => Blake3Managed.Hash(input, output32);

    public static ValueHash256 Hash(ReadOnlySpan<byte> input)
    {
        ValueHash256 result = default;
        Blake3Managed.Hash(input, result.BytesAsSpan);
        return result;
    }

    /// <summary>
    /// The EIP-8297 node hash: 32 zero bytes when both children are zero (an empty subtree),
    /// otherwise BLAKE3 of <paramref name="left"/> concatenated with <paramref name="right"/>.
    /// </summary>
    public static ValueHash256 HashPairOrZero(in ValueHash256 left, in ValueHash256 right)
    {
        if (left == default && right == default) return default;

        ValueHash256 result = default;
        Blake3Managed.HashPair(left.Bytes, right.Bytes, result.BytesAsSpan);
        return result;
    }

    /// <summary>
    /// <see cref="HashPairOrZero"/> for a node whose right child is empty, saving it the check for which
    /// half is zero.
    /// </summary>
    public static ValueHash256 HashWithEmptyRight(in ValueHash256 left)
    {
        if (left == default) return default;

        ValueHash256 result = default;
        Blake3Managed.HashPairHighZero(left.Bytes, result.BytesAsSpan);
        return result;
    }

    /// <summary>
    /// <see cref="HashPairOrZero"/> for a node whose left child is empty, saving it the check for which
    /// half is zero.
    /// </summary>
    public static ValueHash256 HashWithEmptyLeft(in ValueHash256 right)
    {
        if (right == default) return default;

        ValueHash256 result = default;
        Blake3Managed.HashPairLowZero(right.Bytes, result.BytesAsSpan);
        return result;
    }
}
