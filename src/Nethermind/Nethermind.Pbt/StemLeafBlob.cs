// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;

namespace Nethermind.Pbt;

/// <summary>
/// The 256-leaf subtree of one stem, manipulated as a single binary blob: a 32-byte presence
/// bitmap followed by the present 32-byte values packed in ascending sub-index order.
/// </summary>
/// <remarks>
/// Zero values are normalized to absent, matching the EIP-8297 rule that an empty leaf hashes to
/// 32 zero bytes: a stored value is never all-zero, so "present" and "non-zero" coincide. An empty
/// blob (no present leaves) is represented by an empty array and signals stem deletion.
/// Bitmap bits are indexed MSB-first, consistent with stem bit order.
/// </remarks>
public static class StemLeafBlob
{
    public const int ValueLength = 32;
    private const int BitmapLength = 32;
    private const int LeafCount = 256;

    public static bool TryGetValue(ReadOnlySpan<byte> blob, byte subIndex, out ReadOnlySpan<byte> value)
    {
        if (!blob.IsEmpty && IsPresent(blob, subIndex))
        {
            int offset = BitmapLength + ValueLength * CountBefore(blob, subIndex);
            value = blob.Slice(offset, ValueLength);
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Applies <paramref name="changes"/> to <paramref name="blob"/>, returning the new blob and,
    /// via <paramref name="subtreeRoot"/>, its merkelized 256-leaf subtree root. A null or all-zero
    /// value clears the leaf. Returns an empty array (and a zero root) when no leaves remain.
    /// </summary>
    /// <remarks>
    /// Merkelization folds the 256 leaves pairwise over 8 levels (each present value hashes to
    /// <c>blake3(value)</c>, absent leaves are 32 zero bytes, empty subtrees fold to zero). The leaf
    /// level is filled during packing, so this is a single pass over the present values — up to 511
    /// hashes for a full stem; intermediate-level caching is the optimization hook if it ever shows
    /// up in profiles.
    /// </remarks>
    public static byte[] Apply(ReadOnlySpan<byte> blob, IReadOnlyDictionary<byte, byte[]?> changes, out ValueHash256 subtreeRoot)
    {
        Span<byte> bitmap = stackalloc byte[BitmapLength];
        if (!blob.IsEmpty) blob[..BitmapLength].CopyTo(bitmap);

        foreach ((byte subIndex, byte[]? value) in changes)
        {
            if (value is null || value.AsSpan().IsZero())
            {
                bitmap[subIndex >> 3] &= (byte)~(1 << (7 - (subIndex & 7)));
            }
            else
            {
                bitmap[subIndex >> 3] |= (byte)(1 << (7 - (subIndex & 7)));
            }
        }

        int count = 0;
        for (int i = 0; i < BitmapLength; i++) count += BitOperations.PopCount(bitmap[i]);
        if (count == 0)
        {
            subtreeRoot = default;
            return [];
        }

        byte[] result = new byte[BitmapLength + ValueLength * count];
        bitmap.CopyTo(result);
        Span<byte> level = stackalloc byte[LeafCount * ValueLength];
        int offset = BitmapLength;
        for (int subIndex = 0; subIndex < LeafCount; subIndex++)
        {
            if ((bitmap[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) == 0) continue;

            Span<byte> destination = result.AsSpan(offset);
            if (changes.TryGetValue((byte)subIndex, out byte[]? changed))
            {
                changed!.CopyTo(destination);
            }
            else
            {
                TryGetValue(blob, (byte)subIndex, out ReadOnlySpan<byte> existing);
                existing.CopyTo(destination);
            }

            // hash the just-packed leaf value into the merkelization level while it is warm
            Blake3Hash.Hash(destination[..ValueLength], level.Slice(subIndex * ValueLength, ValueLength));
            offset += ValueLength;
        }

        for (int width = LeafCount / 2; width >= 1; width /= 2)
        {
            for (int i = 0; i < width; i++)
            {
                ValueHash256 parent = Blake3Hash.HashPairOrZero(
                    level.Slice(2 * i * ValueLength, ValueLength),
                    level.Slice((2 * i + 1) * ValueLength, ValueLength));
                parent.Bytes.CopyTo(level.Slice(i * ValueLength, ValueLength));
            }
        }

        subtreeRoot = new ValueHash256(level[..ValueLength]);
        return result;
    }

    /// <summary>The stem node hash: <c>blake3(stem || 0x00 || subtreeRoot)</c>.</summary>
    public static ValueHash256 ComputeStemNodeHash(in Stem stem, in ValueHash256 subtreeRoot)
    {
        Span<byte> left = stackalloc byte[32];
        stem.Bytes.CopyTo(left);
        return Blake3Hash.HashPairOrZero(left, subtreeRoot.Bytes);
    }

    private static bool IsPresent(ReadOnlySpan<byte> blob, byte subIndex) =>
        (blob[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) != 0;

    private static int CountBefore(ReadOnlySpan<byte> blob, byte subIndex)
    {
        int count = 0;
        int fullBytes = subIndex >> 3;
        for (int i = 0; i < fullBytes; i++) count += BitOperations.PopCount(blob[i]);
        int remainder = subIndex & 7;
        if (remainder != 0) count += BitOperations.PopCount((uint)(blob[fullBytes] & (byte)(0xFF << (8 - remainder))));
        return count;
    }
}
