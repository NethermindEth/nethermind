// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>
/// Reader-side view of one shard at the version of one RocksDB snapshot: the pinned generations, the version
/// <c>V</c> the snapshot confirms and the flushed marker <c>N</c> below which the snapshot already holds the shard's content.
/// </summary>
internal sealed class TrieNodeLogView(TrieNodeLogShard shard, List<TrieNodeLogGeneration> pinned) : IDisposable
{
    private ulong _version;
    private ulong _flushedGeneration;
    private long _hits;
    private long _chainHits;
    private long _misses;

    public void Bind(IReadOnlyKeyValueStore metadata)
    {
        _version = ReadUInt64(metadata.Get(shard.VersionKey));
        _flushedGeneration = ReadUInt64(metadata.Get(shard.FlushedGenerationKey));
        shard.PinNewer(pinned);

        int flushed = 0;
        while (flushed < pinned.Count && pinned[flushed].Number <= _flushedGeneration) pinned[flushed++].Dispose();
        pinned.RemoveRange(0, flushed);
    }

    public void Dispose()
    {
        foreach (TrieNodeLogGeneration generation in pinned) generation.Dispose();
        pinned.Clear();
        if (_hits != 0) Metrics.TrieNodeLogReads.AddBy(TrieNodeLogLabel.Hit, _hits);
        if (_chainHits != 0) Metrics.TrieNodeLogReads.AddBy(TrieNodeLogLabel.Chain, _chainHits);
        if (_misses != 0) Metrics.TrieNodeLogReads.AddBy(TrieNodeLogLabel.Miss, _misses);
        shard.RefreshGauges();
    }

    /// <summary>Whether the shard holds the value for <paramref name="key"/> at this view's version; <paramref name="value"/> is null for a tombstone.</summary>
    [SkipLocalsInit]
    public bool TryGet(byte column, ReadOnlySpan<byte> key, out byte[]? value)
    {
        ulong hash = TrieNodeLogRecord.Hash(column, key);
        Span<byte> stored = stackalloc byte[TrieNodeLogBlock.MaxStoredLength];
        Span<byte> raw = stackalloc byte[TrieNodeLogBlock.Size];
        for (int i = pinned.Count - 1; i >= 0; i--)
        {
            TrieNodeLogGeneration generation = pinned[i];
            if (!generation.TryLocate(hash, column, key, stored, raw, out _, out TrieNodeLogGeneration.BlockHit hit)) continue;

            // A block holds one batch, so the record found has the block's version; an older version is in the
            // block its prev link names, where the key again has exactly one live record.
            bool walked = false;
            while (hit.Header.Version > _version)
            {
                ulong previous = hit.Header.Prev;
                if (previous == 0 || TrieNodeLogRecord.LocationGeneration(previous) <= _flushedGeneration)
                {
                    Interlocked.Increment(ref _misses);
                    value = null;
                    return false;
                }

                generation = Pinned(TrieNodeLogRecord.LocationGeneration(previous));
                long blockOffset = TrieNodeLogRecord.LocationOffset(previous);
                int rawLength = generation.ReadBlock(blockOffset, stored, raw);
                TrieNodeLogRecord header = default;
                int start = rawLength < 0 ? -1 : TrieNodeLogBlock.FindRecord(raw[..rawLength], column, key, out header);
                if (start < 0)
                {
                    Metrics.RecordTrieNodeLogChainKeyMismatch();
                    throw new InvalidOperationException($"Trie node log block at {generation.Path}:{blockOffset} is linked as holding a previous version of a key it does not contain");
                }

                hit = new TrieNodeLogGeneration.BlockHit(blockOffset, rawLength, start, header);
                walked = true;
            }

            Interlocked.Increment(ref walked ? ref _chainHits : ref _hits);
            value = hit.Header.Type == TrieNodeLogRecord.Delete
                ? null
                : raw.Slice(hit.RecordStart + TrieNodeLogRecord.HeaderLength + hit.Header.KeyLength, hit.Header.ValueLength).ToArray();
            return true;
        }

        Interlocked.Increment(ref _misses);
        value = null;
        return false;
    }

    private TrieNodeLogGeneration Pinned(ulong number)
    {
        // Generations are pinned contiguously above the flushed marker, so the index is an offset from the oldest.
        TrieNodeLogGeneration generation = pinned[(int)(number - pinned[0].Number)];
        return generation.Number == number ? generation : throw new InvalidOperationException($"Trie node log generation {number} is not pinned");
    }

    private static ulong ReadUInt64(byte[]? bytes) => bytes is { Length: 8 } ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : 0;
}
