// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Calculates an EIP-8297 root from strictly ordered, prefix-free image leaves.</summary>
internal static class PbtImageRootCalculator
{
    internal static ValueHash256 Calculate(IEnumerable<RebuildEntry> entries, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IEnumerator<RebuildEntry> enumerator = entries.GetEnumerator();
        if (!enumerator.MoveNext()) return default;

        Frame[] frames = new Frame[PbtStorageFullKey.MaxLength * 8];
        Span<byte> preimage = stackalloc byte[3 + PbtStorageFullKey.MaxLength + 64];
        int frameCount = 0;
        RebuildEntry previous = enumerator.Current;
        ValueHash256 current = HashLeaf(previous, preimage);
        while (enumerator.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RebuildEntry next = enumerator.Current;
            if (previous.Key.CompareTo(next.Key) >= 0)
                throw new InvalidDataException("Image leaves must be strictly ordered.");
            int differingBit = previous.Key.FirstDifferingBit(next.Key);
            if (differingBit == Math.Min(previous.Key.BitLength, next.Key.BitLength))
                throw new InvalidDataException("Image keys must be prefix-free.");

            Fold(differingBit, previous.Key, frames, ref frameCount, ref current, preimage);
            frames[frameCount++] = new(differingBit, current);
            current = HashLeaf(next, preimage);
            previous = next;
        }

        cancellationToken.ThrowIfCancellationRequested();
        Fold(-1, previous.Key, frames, ref frameCount, ref current, preimage);
        return current;
    }

    private static ValueHash256 HashLeaf(in RebuildEntry entry, Span<byte> preimage)
    {
        if (entry.Key.Length == 0 || entry.Leaf == default)
            throw new InvalidDataException("Image leaves must have a nonempty key and nonzero value.");
        preimage[0] = 0;
        entry.Key.Bytes.CopyTo(preimage[1..]);
        entry.Leaf.Bytes.CopyTo(preimage[(1 + entry.Key.Length)..]);
        return Blake3Hash.Hash(preimage[..(1 + entry.Key.Length + 32)]);
    }

    private static void Fold(int nextSplit, in PbtStorageFullKey key, ReadOnlySpan<Frame> frames,
        ref int frameCount, ref ValueHash256 current, Span<byte> preimage)
    {
        // EIP-8297 prefixes start after the parent split. The next key reveals that parent
        // only once its common prefix is shorter than the completed branch's split depth.
        while (frameCount > 0 && frames[frameCount - 1].SplitBit > nextSplit)
        {
            Frame frame = frames[--frameCount];
            int parentSplit = frameCount == 0 ? nextSplit : Math.Max(nextSplit, frames[frameCount - 1].SplitBit);
            int prefixStart = parentSplit + 1;
            int prefixBits = frame.SplitBit - prefixStart;
            int prefixBytes = (prefixBits + 7) / 8;
            preimage[0] = 1;
            BinaryPrimitives.WriteUInt16BigEndian(preimage[1..], (ushort)prefixBits);
            Span<byte> prefix = preimage.Slice(3, prefixBytes);
            prefix.Clear();
            for (int bit = 0; bit < prefixBits; bit++)
                prefix[bit / 8] |= (byte)(key.GetBit(prefixStart + bit) << (7 - bit % 8));
            frame.Left.Bytes.CopyTo(preimage[(3 + prefixBytes)..]);
            current.Bytes.CopyTo(preimage[(3 + prefixBytes + 32)..]);
            current = Blake3Hash.Hash(preimage[..(3 + prefixBytes + 64)]);
        }
    }

    private readonly record struct Frame(int SplitBit, ValueHash256 Left);
}
