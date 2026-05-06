// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Autofac.Features.AttributeFilters;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Snapshots;

internal sealed class StateCompositionSnapshotStore
{
    public const string DbName = "stateComposition";

    private static readonly StateCompositionSnapshotDecoder Decoder = StateCompositionSnapshotDecoder.Instance;

    // long.MaxValue big-endian: theoretical collision with a real block-number key
    // lands ~3.5 trillion years out at 12s blocks, so no schema migration needed.
    private static readonly byte[] LatestKey = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    // The three tracker maps scale with the contract count (134M+ on mainnet,
    // ~5 GB encoded) and would overflow the int-bounded RLP buffer if written as
    // a single blob. They are written as fixed-width chunks under
    // <blockNumber:8> || <kind:1> || <chunkIdx:4> = 13-byte composite keys,
    // distinguished from the 8-byte main-snapshot key.
    private const byte SlotCountKind = 0x01;
    private const byte CodeRefcountKind = 0x02;
    private const byte CodeSizeKind = 0x03;
    private const int DefaultEntriesPerChunk = 1_000_000;
    private const int MainKeyLength = 8;
    private const int ChunkKeyLength = 13;

    private readonly IDb _db;
    private readonly int _entriesPerChunk;
    private readonly ILogger _logger;

    public StateCompositionSnapshotStore(
        [KeyFilter("stateComposition")] IDb db,
        ILogManager logManager)
        : this(db, logManager, DefaultEntriesPerChunk)
    {
    }

    internal StateCompositionSnapshotStore(IDb db, ILogManager logManager, int entriesPerChunk)
    {
        _db = db;
        _entriesPerChunk = entriesPerChunk;
        _logger = logManager.GetClassLogger<StateCompositionSnapshotStore>();
    }

    public void WriteSnapshot(StateCompositionSnapshot snapshot)
    {
        // Write order: main blob → map chunks → LatestKey → purge old block. A
        // crash anywhere before the LatestKey update leaves the previous block
        // reachable; a crash after leaves the next boot's PurgeOldEntries to
        // reconcile orphaned partial chunks.
        long prevBlock = long.MinValue;
        byte[]? prevBytes = _db.Get(LatestKey);
        if (prevBytes is not null && prevBytes.Length >= MainKeyLength)
            prevBlock = BinaryPrimitives.ReadInt64BigEndian(prevBytes);

        Span<byte> key = stackalloc byte[MainKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(key, snapshot.BlockNumber);

        int length = Decoder.GetLength(snapshot);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            RlpStream stream = new(buffer);
            Decoder.Encode(stream, snapshot);
            _db.PutSpan(key, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        WriteLongMap(snapshot.BlockNumber, SlotCountKind, snapshot.SlotCountByAddress);
        WriteIntMap(snapshot.BlockNumber, CodeRefcountKind, snapshot.CodeHashRefcounts);
        WriteIntMap(snapshot.BlockNumber, CodeSizeKind, snapshot.CodeHashSizes);

        Span<byte> blockBytes = stackalloc byte[MainKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(blockBytes, snapshot.BlockNumber);
        _db.PutSpan(LatestKey, blockBytes);

        if (prevBlock != long.MinValue && prevBlock != snapshot.BlockNumber)
            RemoveBlockEntries(prevBlock);
    }

    public StateCompositionSnapshot? ReadSnapshot(long blockNumber)
    {
        Span<byte> key = stackalloc byte[MainKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(key, blockNumber);

        byte[]? data = _db.Get(key);
        if (data is null) return null;

        StateCompositionSnapshot snapshot;
        try
        {
            Rlp.ValueDecoderContext ctx = data.AsRlpValueContext();
            snapshot = Decoder.Decode(ref ctx);
        }
        catch (Exception ex) when (ex is RlpException or InvalidDataException or EndOfStreamException or IOException)
        {
            if (_logger.IsWarn)
                _logger.Warn($"StateComposition: persisted snapshot at block {blockNumber} could not be decoded " +
                             $"(reason: {ex.Message}). Falling back to a fresh scan to rebuild the cached baseline.");
            return null;
        }

        Dictionary<ValueHash256, long> slotCounts = ReadLongMap(blockNumber, SlotCountKind);
        Dictionary<ValueHash256, int> codeRefcounts = ReadIntMap(blockNumber, CodeRefcountKind);
        Dictionary<ValueHash256, int> codeSizes = ReadIntMap(blockNumber, CodeSizeKind);

        return snapshot with
        {
            SlotCountByAddress = slotCounts,
            CodeHashRefcounts = codeRefcounts,
            CodeHashSizes = codeSizes,
        };
    }

    public StateCompositionSnapshot? ReadLatestSnapshot()
    {
        byte[]? latestBytes = _db.Get(LatestKey);
        if (latestBytes is null || latestBytes.Length < MainKeyLength) return null;

        long latestBlock = BinaryPrimitives.ReadInt64BigEndian(latestBytes);
        return ReadSnapshot(latestBlock);
    }

    public void PurgeOldEntries()
    {
        byte[]? latestBytes = _db.Get(LatestKey);
        long latestBlock = -1;
        if (latestBytes is not null && latestBytes.Length >= MainKeyLength)
            latestBlock = BinaryPrimitives.ReadInt64BigEndian(latestBytes);

        int removed = 0;
        foreach (byte[] key in _db.GetAllKeys())
        {
            ReadOnlySpan<byte> span = key;
            if (span.SequenceEqual(LatestKey)) continue;

            long blockOfKey = (span.Length == MainKeyLength || span.Length == ChunkKeyLength)
                ? BinaryPrimitives.ReadInt64BigEndian(span[..MainKeyLength])
                : long.MinValue;

            if (latestBlock >= 0 && blockOfKey == latestBlock) continue;

            _db.Remove(key);
            removed++;
        }

        if (removed > 0 && _logger.IsInfo)
            _logger.Info($"StateComposition: purged {removed} old snapshot entries");
    }

    private void RemoveBlockEntries(long blockNumber)
    {
        Span<byte> mainKey = stackalloc byte[MainKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(mainKey, blockNumber);
        _db.Remove(mainKey);

        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(chunkKey[..MainKeyLength], blockNumber);
        foreach (byte kind in (ReadOnlySpan<byte>)[SlotCountKind, CodeRefcountKind, CodeSizeKind])
        {
            chunkKey[8] = kind;
            int chunkIdx = 0;
            while (true)
            {
                BinaryPrimitives.WriteInt32BigEndian(chunkKey[9..13], chunkIdx);
                if (_db.Get(chunkKey) is null) break;
                _db.Remove(chunkKey);
                chunkIdx++;
            }
        }
    }

    private void WriteLongMap(long blockNumber, byte kind, IReadOnlyDictionary<ValueHash256, long> map)
    {
        if (map.Count == 0) return;

        const int entrySize = 32 + 8;
        using IEnumerator<KeyValuePair<ValueHash256, long>> entries = map.GetEnumerator();
        WriteMap(blockNumber, kind, map.Count, entries, entrySize, static (kvp, dst) =>
        {
            kvp.Key.Bytes.CopyTo(dst);
            BinaryPrimitives.WriteInt64BigEndian(dst[32..40], kvp.Value);
        });
    }

    private void WriteIntMap(long blockNumber, byte kind, IReadOnlyDictionary<ValueHash256, int> map)
    {
        if (map.Count == 0) return;

        const int entrySize = 32 + 4;
        using IEnumerator<KeyValuePair<ValueHash256, int>> entries = map.GetEnumerator();
        WriteMap(blockNumber, kind, map.Count, entries, entrySize, static (kvp, dst) =>
        {
            kvp.Key.Bytes.CopyTo(dst);
            BinaryPrimitives.WriteInt32BigEndian(dst[32..36], kvp.Value);
        });
    }

    private delegate void EntryWriter<TValue>(KeyValuePair<ValueHash256, TValue> kvp, Span<byte> dst);

    private void WriteMap<TValue>(
        long blockNumber,
        byte kind,
        int count,
        IEnumerator<KeyValuePair<ValueHash256, TValue>> entries,
        int entrySize,
        EntryWriter<TValue> writeEntry)
    {
        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(chunkKey[..MainKeyLength], blockNumber);
        chunkKey[8] = kind;

        int chunkIdx = 0;
        int remaining = count;
        while (remaining > 0)
        {
            int chunkCount = Math.Min(_entriesPerChunk, remaining);
            int payloadLength = 4 + chunkCount * entrySize;
            byte[] buf = ArrayPool<byte>.Shared.Rent(payloadLength);
            try
            {
                BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0, 4), chunkCount);
                int pos = 4;
                for (int i = 0; i < chunkCount; i++)
                {
                    if (!entries.MoveNext())
                        throw new InvalidOperationException("StateComposition snapshot: dictionary mutated during persistence");
                    writeEntry(entries.Current, buf.AsSpan(pos, entrySize));
                    pos += entrySize;
                }
                BinaryPrimitives.WriteInt32BigEndian(chunkKey[9..13], chunkIdx);
                _db.PutSpan(chunkKey, buf.AsSpan(0, payloadLength));
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            remaining -= chunkCount;
            chunkIdx++;
        }
    }

    private Dictionary<ValueHash256, long> ReadLongMap(long blockNumber, byte kind)
    {
        Dictionary<ValueHash256, long> result = [];
        ReadMap(blockNumber, kind, 32 + 8, (entry, dict) =>
        {
            ValueHash256 hash = new(entry[..32]);
            long value = BinaryPrimitives.ReadInt64BigEndian(entry[32..40]);
            dict[hash] = value;
        }, result);
        return result;
    }

    private Dictionary<ValueHash256, int> ReadIntMap(long blockNumber, byte kind)
    {
        Dictionary<ValueHash256, int> result = [];
        ReadMap(blockNumber, kind, 32 + 4, (entry, dict) =>
        {
            ValueHash256 hash = new(entry[..32]);
            int value = BinaryPrimitives.ReadInt32BigEndian(entry[32..36]);
            dict[hash] = value;
        }, result);
        return result;
    }

    private void ReadMap<TDict>(
        long blockNumber,
        byte kind,
        int entrySize,
        EntryReader<TDict> readEntry,
        TDict dict)
    {
        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        BinaryPrimitives.WriteInt64BigEndian(chunkKey[..MainKeyLength], blockNumber);
        chunkKey[8] = kind;
        int chunkIdx = 0;
        while (true)
        {
            BinaryPrimitives.WriteInt32BigEndian(chunkKey[9..13], chunkIdx);
            byte[]? data = _db.Get(chunkKey);
            if (data is null) break;

            // Disk-read boundary: validate the chunk-count prefix and the
            // declared payload length before slicing, so a truncated or
            // mid-read corrupted chunk degrades to "no more entries"
            // instead of throwing ArgumentOutOfRangeException up the stack.
            // Truncation mid-sequence (chunkIdx > 0) leaves a partially-loaded
            // map; warn once so a follow-up rescan can be diagnosed and the
            // discrepancy isn't silent.
            if (data.Length < 4)
            {
                LogChunkTruncated(blockNumber, kind, chunkIdx);
                break;
            }
            int chunkCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
            if (chunkCount < 0 || data.Length < 4 + (long)chunkCount * entrySize)
            {
                LogChunkTruncated(blockNumber, kind, chunkIdx);
                break;
            }
            int pos = 4;
            for (int i = 0; i < chunkCount; i++)
            {
                readEntry(data.AsSpan(pos, entrySize), dict);
                pos += entrySize;
            }
            chunkIdx++;
        }
    }

    private delegate void EntryReader<TDict>(ReadOnlySpan<byte> entry, TDict dict);

    private void LogChunkTruncated(long blockNumber, byte kind, int chunkIdx)
    {
        if (chunkIdx > 0 && _logger.IsWarn)
            _logger.Warn($"StateComposition: corrupt chunk {chunkIdx} for block {blockNumber} kind {kind:x2}; " +
                         $"loaded {chunkIdx} valid chunk(s) before truncation. Plugin will rescan.");
    }
}
