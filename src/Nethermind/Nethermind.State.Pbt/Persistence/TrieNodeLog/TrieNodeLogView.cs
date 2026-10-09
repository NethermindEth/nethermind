// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;

namespace Nethermind.State.Pbt.Persistence.TrieNodeLog;

/// <summary>
/// Reader-side view of one shard at the version of one RocksDB snapshot: the pinned generations, the version
/// <c>V</c> the snapshot confirms and the flushed marker <c>N</c> below which the snapshot already holds the shard's content.
/// </summary>
public sealed class TrieNodeLogView(TrieNodeLogShard shard, ArrayPoolList<TrieNodeLogGeneration> pinned) : IDisposable
{
    // Header, the longest key and a typical group fit in one read; a longer value takes a second.
    private const int ReadBufferSize = 4096;

    private ulong _version;
    private ulong _flushedGeneration;
    private bool _bound;
    private long _hits;
    private long _chainHits;
    private long _misses;

    /// <summary>Binds the view to its snapshot's version; on the thread that pinned, which holds the shard's retention until this returns.</summary>
    public void Bind(IReadOnlyKeyValueStore metadata)
    {
        try
        {
            _version = TrieNodeLogShard.ReadUInt64(metadata.Get(shard.VersionKey));
            _flushedGeneration = TrieNodeLogShard.ReadUInt64(metadata.Get(shard.FlushedGenerationKey));
            shard.PinNewer(pinned);

            while (pinned.Count > 0 && pinned[0].Number <= _flushedGeneration)
            {
                pinned[0].Dispose();
                pinned.RemoveAt(0);
            }
        }
        finally
        {
            _bound = true;
            shard.EndOpening();
        }
    }

    public void Dispose()
    {
        if (!_bound)
        {
            _bound = true;
            shard.EndOpening();
        }
        pinned.DisposeRecursive();
        bool secondLevel = shard.IsSecondLevel;
        if (_hits != 0) Metrics.PbtTrieNodeLogReads.AddBy(secondLevel ? TrieNodeLog.SecondLevelHitLabel : TrieNodeLog.HitLabel, _hits);
        if (_chainHits != 0) Metrics.PbtTrieNodeLogReads.AddBy(secondLevel ? TrieNodeLog.SecondLevelChainLabel : TrieNodeLog.ChainLabel, _chainHits);
        if (_misses != 0) Metrics.PbtTrieNodeLogReads.AddBy(secondLevel ? TrieNodeLog.SecondLevelMissLabel : TrieNodeLog.MissLabel, _misses);
    }

    /// <summary>Whether the shard holds the value for <paramref name="key"/> at this view's version; <paramref name="value"/> is null for a tombstone.</summary>
    public bool TryGet(ReadOnlySpan<byte> key, out byte[]? value)
    {
        ulong hash = TrieNodeLogRecord.Hash(key);
        Span<byte> buffer = stackalloc byte[ReadBufferSize];
        for (int i = pinned.Count - 1; i >= 0; i--)
        {
            TrieNodeLogGeneration generation = pinned[i];
            if (!generation.TryLocate(hash, key, buffer, out TrieNodeLogRecord header, out _, out long offset, out int bytesRead)) continue;

            // Walk the key's versions within this generation; once they run out, the next older generation's index
            // takes over, and RocksDB holds whatever the pinned generations do not.
            bool walked = false;
            bool found = true;
            while (header.Version > _version)
            {
                if (header.Prev == 0)
                {
                    found = false;
                    break;
                }

                offset = TrieNodeLogRecord.PrevOffset(header.Prev);
                bytesRead = generation.ReadAt(offset, buffer);
                if (!TrieNodeLogRecord.HoldsKey(buffer[..bytesRead], key, out header))
                {
                    throw new InvalidOperationException($"Trie node log record at {generation.Path}:{offset} is linked as a previous version of a different key");
                }
                walked = true;
            }
            if (!found) continue;

            Interlocked.Increment(ref walked ? ref _chainHits : ref _hits);
            value = header.Type == TrieNodeLogRecord.Delete ? null : generation.ReadValue(offset, header, buffer, bytesRead);
            return true;
        }

        Interlocked.Increment(ref _misses);
        value = null;
        return false;
    }
}
