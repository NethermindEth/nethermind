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

    /// <summary>Hashes a pair with an empty right child.</summary>
    public static ValueHash256 HashWithEmptyRight(in ValueHash256 left)
    {
        if (left == default) return default;

        ValueHash256 result = default;
        Blake3Managed.HashPairHighZero(left.Bytes, result.BytesAsSpan);
        return result;
    }

    /// <summary>Hashes a pair with an empty left child.</summary>
    public static ValueHash256 HashWithEmptyLeft(in ValueHash256 right)
    {
        if (right == default) return default;

        ValueHash256 result = default;
        Blake3Managed.HashPairLowZero(right.Bytes, result.BytesAsSpan);
        return result;
    }

    internal static ValueHash256 FoldFour(ReadOnlySpan<byte> compactSources, byte presenceMask)
    {
        if (presenceMask == 0) return default;

        ValueHash256 result = default;
        Blake3Managed.FoldFour(compactSources, presenceMask, result.BytesAsSpan);
        return result;
    }

    internal static ValueHash256 FoldEight(ReadOnlySpan<byte> compactSources, byte presenceMask)
    {
        if (presenceMask == 0) return default;

        ValueHash256 result = default;
        Blake3Managed.FoldEight(compactSources, presenceMask, result.BytesAsSpan);
        return result;
    }

    internal static ValueHash256 FoldSixteen(ReadOnlySpan<byte> compactSources, ushort presenceMask)
    {
        if (presenceMask == 0) return default;

        ValueHash256 result = default;
        Blake3Managed.FoldSixteen(compactSources, presenceMask, result.BytesAsSpan);
        return result;
    }
}
