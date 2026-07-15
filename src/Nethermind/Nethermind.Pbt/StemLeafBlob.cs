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
    /// Applies <paramref name="changes"/> to <paramref name="blob"/> and returns the new blob.
    /// A null or all-zero value clears the leaf. Returns an empty array when no leaves remain.
    /// </summary>
    public static byte[] Apply(ReadOnlySpan<byte> blob, IReadOnlyDictionary<byte, byte[]?> changes)
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
        if (count == 0) return [];

        byte[] result = new byte[BitmapLength + ValueLength * count];
        bitmap.CopyTo(result);
        int offset = BitmapLength;
        for (int subIndex = 0; subIndex < LeafCount; subIndex++)
        {
            if ((bitmap[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) == 0) continue;

            if (changes.TryGetValue((byte)subIndex, out byte[]? changed))
            {
                changed!.CopyTo(result.AsSpan(offset));
            }
            else
            {
                TryGetValue(blob, (byte)subIndex, out ReadOnlySpan<byte> existing);
                existing.CopyTo(result.AsSpan(offset));
            }

            offset += ValueLength;
        }

        return result;
    }

    /// <summary>
    /// Merkelizes the 256 leaves: each present value hashes to <c>blake3(value)</c>, absent leaves
    /// are 32 zero bytes, folded pairwise over 8 levels with the empty-subtree-to-zero rule.
    /// </summary>
    // Recomputes all 8 levels (up to 511 hashes) per call; intermediate-level caching is the
    // obvious optimization hook if this shows up in profiles.
    public static ValueHash256 ComputeSubtreeRoot(ReadOnlySpan<byte> blob)
    {
        if (blob.IsEmpty) return default;

        Span<byte> level = stackalloc byte[LeafCount * ValueLength];
        for (int subIndex = 0; subIndex < LeafCount; subIndex++)
        {
            Span<byte> slot = level.Slice(subIndex * ValueLength, ValueLength);
            if (TryGetValue(blob, (byte)subIndex, out ReadOnlySpan<byte> value))
            {
                Blake3Hash.Hash(value, slot);
            }
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

        return new ValueHash256(level[..ValueLength]);
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
