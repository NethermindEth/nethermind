// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>
/// Assembles a <see cref="PbtLeafFormat.LeavesOnly"/> <see cref="StemLeafBlob"/> from leaves handed in
/// in any order, computing no hash at all.
/// </summary>
/// <remarks>
/// <see cref="StemLeafBlob.Apply"/> merkelizes as it writes, which is what a bulk load wants to avoid:
/// a rebuild folds the whole tree afterwards anyway, so the leaves it starts from need only be laid
/// out, not hashed. That is exactly what the leaves-only layout stores, so this writes the entries and
/// the bitmap footer and nothing else.
/// <para>
/// The instance carries a full stem's worth of values (8 KiB) and is meant to be reused across stems
/// via <see cref="Reset"/>. It is not thread-safe.
/// </para>
/// </remarks>
public sealed class StemLeafBlobBuilder
{
    private const int LeafCount = 256;

    private readonly byte[] _values = new byte[LeafCount * StemLeafBlob.ValueLength];
    private readonly byte[] _bitmap = new byte[TwoLevelBitmapReader.BitmapLength];
    private int _leafCount;

    /// <summary>Whether no leaf is present, in which case the stem has no blob at all.</summary>
    public bool IsEmpty => _leafCount == 0;

    public void Reset()
    {
        _bitmap.AsSpan().Clear();
        _leafCount = 0;
    }

    /// <summary>Sets leaf <paramref name="subIndex"/>; an all-zero (or empty) value clears it, as the blob normalizes zero to absent.</summary>
    /// <param name="value">Up to <see cref="StemLeafBlob.ValueLength"/> bytes, right-aligned in the leaf as the tree stores it.</param>
    public void Set(byte subIndex, scoped ReadOnlySpan<byte> value)
    {
        Span<byte> leaf = _values.AsSpan(subIndex * StemLeafBlob.ValueLength, StemLeafBlob.ValueLength);
        leaf.Clear();
        value.CopyTo(leaf[(StemLeafBlob.ValueLength - value.Length)..]);
        SetPresent(subIndex, leaf.ContainsAnyExcept((byte)0));
    }

    /// <summary>Loads the leaves of an existing leaves-only blob, so more can be merged into it.</summary>
    /// <remarks>Replaces whatever the builder held; call it before the <see cref="Set"/> calls that merge on top.</remarks>
    /// <exception cref="System.IO.InvalidDataException">The blob is not in <see cref="PbtLeafFormat.LeavesOnly"/>.</exception>
    public void Seed(scoped ReadOnlySpan<byte> blob)
    {
        Reset();
        if (blob.IsEmpty) return;

        StemLeafBlob.LeafEnumerator leaves = StemLeafBlob.EnumerateLeavesOnly(blob);
        while (leaves.MoveNext())
        {
            Set(leaves.CurrentSubIndex, leaves.CurrentValue);
        }
    }

    /// <summary>The bytes of the assembled blob; empty when <see cref="IsEmpty"/>, which marks the stem as having none.</summary>
    public byte[] Encode()
    {
        if (_leafCount == 0) return [];

        int occupiedGroups = TwoLevelBitmapReader.OccupiedGroupsOf(_bitmap);
        byte[] blob = new byte[_leafCount * StemLeafBlob.ValueLength
            + occupiedGroups * TwoLevelBitmapReader.SubWordLength
            + TwoLevelBitmapReader.TopLength
            + TwoLevelBitmapReader.FormatLength];

        // leaves-only entries are the present leaves in ascending sub-index order, which is also
        // their post-order, there being no internal node to come between them
        int slot = 0;
        for (int subIndex = 0; subIndex < LeafCount; subIndex++)
        {
            if (!IsPresent(subIndex)) continue;

            _values.AsSpan(subIndex * StemLeafBlob.ValueLength, StemLeafBlob.ValueLength)
                .CopyTo(blob.AsSpan(slot * StemLeafBlob.ValueLength, StemLeafBlob.ValueLength));
            slot++;
        }

        TwoLevelBitmapReader.Encode(_bitmap, blob.AsSpan(_leafCount * StemLeafBlob.ValueLength), PbtLeafFormat.LeavesOnly);
        return blob;
    }

    private bool IsPresent(int subIndex) => (_bitmap[subIndex >> 3] & (1 << (7 - (subIndex & 7)))) != 0;

    private void SetPresent(byte subIndex, bool present)
    {
        if (IsPresent(subIndex) == present) return;

        byte mask = (byte)(1 << (7 - (subIndex & 7)));
        if (present)
        {
            _bitmap[subIndex >> 3] |= mask;
            _leafCount++;
        }
        else
        {
            _bitmap[subIndex >> 3] &= (byte)~mask;
            _leafCount--;
        }
    }
}
