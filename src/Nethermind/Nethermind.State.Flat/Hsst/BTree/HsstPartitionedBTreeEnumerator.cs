// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State.Flat.Hsst.BTree;

/// <summary>
/// Forward iterator over an <see cref="IndexType.PartitionedBTreeKeyFirst"/> (0x08) or
/// <see cref="IndexType.PartitionedBTree"/> (0x0A) blob for
/// <see cref="HsstEnumerator{TReader,TPin}"/>. Walks the directory B-tree left-to-right and
/// drains each partition's inner B-tree in turn; the per-partition hashtables are ignored.
/// Because partitions are key-ordered and each inner index is internally sorted, the
/// concatenated walk yields the same key-sorted sequence as the equivalent 0x07/0x01 blob.
/// </summary>
/// <remarks>
/// The directory is always a key-first B-tree (its values are the metadata records);
/// <paramref name="keyFirst"/> selects the per-partition entry layout: true for 0x08,
/// false for 0x0A (key-after-value).
/// </remarks>
internal sealed class HsstPartitionedBTreeEnumerator<TReader, TPin>
    where TPin : struct, IBufferPin, allows ref struct
    where TReader : IHsstByteReader<TPin>, allows ref struct
{
    private readonly long _scopeStart;
    private readonly int _keyLength;
    private readonly bool _keyFirst;
    private readonly HsstBTreeEnumerator<TReader, TPin> _directory;
    private HsstBTreeEnumerator<TReader, TPin>? _partition;
    private bool _done;

    public HsstPartitionedBTreeEnumerator(scoped in TReader reader, Bound scope, bool keyFirst = true)
    {
        _scopeStart = scope.Offset;
        _keyFirst = keyFirst;
        // KeyLength sits at scope end − 2 (before the IndexType byte). The directory shares
        // the 0x08 trailer and is a key-first B-tree, so the ordinary trailer-parsing
        // enumerator constructor walks it directly.
        int keyLength = 0;
        if (scope.Length >= 2)
        {
            Span<byte> kl = stackalloc byte[1];
            if (reader.TryRead(scope.Offset + scope.Length - 2, kl)) keyLength = kl[0];
        }
        _keyLength = keyLength;
        _directory = new HsstBTreeEnumerator<TReader, TPin>(in reader, scope, keyFirst: true);
    }

    public long Count => -1;

    public bool MoveNext(scoped in TReader reader)
    {
        if (_done) return false;
        while (true)
        {
            if (_partition is not null && _partition.MoveNext(in reader)) return true;

            // Current partition drained (or none yet) — advance to the next directory entry.
            if (!_directory.MoveNext(in reader))
            {
                _partition = null;
                _done = true;
                return false;
            }
            _partition = OpenPartition(in reader, _directory.CurrentValue);
        }
    }

    private HsstBTreeEnumerator<TReader, TPin>? OpenPartition(scoped in TReader reader, Bound metaBound)
    {
        if (metaBound.Length < HsstPartitionHashtable.DirRecordFixedSize) return EmptyPartition();
        Span<byte> rec = stackalloc byte[HsstPartitionHashtable.DirRecordFixedSize];
        if (!reader.TryRead(metaBound.Offset, rec)) return EmptyPartition();
        long innerRootOffset = ReadU48(rec);
        long innerBufferEnd = ReadU48(rec[6..]);
        int rootPrefixLen = rec[27];

        byte[] rootPrefix = [];
        if (rootPrefixLen > 0)
        {
            rootPrefix = new byte[rootPrefixLen];
            if (!reader.TryRead(metaBound.Offset + HsstPartitionHashtable.DirRecordFixedSize, rootPrefix))
                return EmptyPartition();
        }

        return new HsstBTreeEnumerator<TReader, TPin>(
            _scopeStart, _scopeStart + innerBufferEnd, _scopeStart + innerRootOffset, rootPrefix, _keyLength, keyFirst: _keyFirst);
    }

    // A malformed/short metadata record yields an empty partition rather than throwing mid-walk;
    // MoveNext then advances to the next directory entry.
    private HsstBTreeEnumerator<TReader, TPin> EmptyPartition() =>
        new(_scopeStart, _scopeStart, -1, [], _keyLength, keyFirst: _keyFirst);

    public Bound CurrentKey => _partition?.CurrentKey ?? default;
    public Bound CurrentValue => _partition?.CurrentValue ?? default;
    public long CurrentMetadataStart => _partition?.CurrentMetadataStart ?? 0;

    private static long ReadU48(scoped ReadOnlySpan<byte> src) =>
        src[0]
        | ((long)src[1] << 8)
        | ((long)src[2] << 16)
        | ((long)src[3] << 24)
        | ((long)src[4] << 32)
        | ((long)src[5] << 40);
}
