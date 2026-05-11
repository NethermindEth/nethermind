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

/// <summary>
/// Persists the StateComposition plugin's cached baseline so a restart doesn't
/// require a multi-hour full state scan.
///
/// Storage layout (generation-rotated, stable-prefix):
/// <list type="bullet">
///   <item><c>LatestKey</c> (8 bytes of 0xFF) → 9-byte value: <c>&lt;gen:1&gt;&lt;blockNumber:8&gt;</c>.
///         Atomic commit point — RocksDB guarantees per-key Put atomicity.</item>
///   <item>Main blob: <c>&lt;gen:1&gt;&lt;0xFE:1&gt;</c> (2 bytes). Value = RLP-encoded scalar stats + depth distribution.</item>
///   <item>Tracker-map chunk: <c>&lt;gen:1&gt;&lt;kind:1&gt;&lt;chunkIdx:4&gt;</c> (6 bytes). Value = fixed-width entries.</item>
/// </list>
///
/// Each <see cref="WriteSnapshot"/> writes into the *other* generation, then
/// flips the gen byte in <c>LatestKey</c>, then deletes the previous generation.
/// This bounds the on-disk footprint to two generations (worst case during a
/// flush) and avoids the put-then-delete tombstone pattern that previously
/// accumulated thousands of orphaned SSTs in RocksDB (1.6 TB observed in a
/// half-day bloating run before this fix).
///
/// </summary>
internal sealed class StateCompositionSnapshotStore
{
    public const string DbName = "stateComposition";

    private static readonly StateCompositionSnapshotDecoder Decoder = StateCompositionSnapshotDecoder.Instance;

    // long.MaxValue big-endian: theoretical collision with a real block-number key
    // lands ~3.5 trillion years out at 12s blocks, so no schema migration needed.
    private static readonly byte[] LatestKey = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];

    // Marker byte for the main-blob key, picked so it does not collide with any
    // tracker-map kind below. Both share the same gen-prefixed namespace.
    private const byte MainBlobMarker = 0xFE;

    // The three tracker maps scale with the contract count (147 M on bloatnet,
    // ~5 GB encoded each) and would overflow the int-bounded RLP buffer if
    // written as a single blob. They are written as fixed-width chunks keyed
    // by <gen:1> || <kind:1> || <chunkIdx:4> = 6-byte composite keys.
    private const byte SlotCountKind = 0x01;
    private const byte CodeRefcountKind = 0x02;
    private const byte CodeSizeKind = 0x03;
    private const int DefaultEntriesPerChunk = 1_000_000;

    private const int ChunkKeyLength = 6;
    private const int MainBlobKeyLength = 2;
    private const int LatestValueLength = 9;       // <gen:1><blockNumber:8>

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
        // 1. Determine the live generation. If LatestKey is absent (first
        //    write) start at gen 0; next write flips to 1. We always write to
        //    the OTHER generation so the current one stays intact until the
        //    commit at step 3.
        byte currentGen = ReadCurrentGen();
        byte nextGen = (byte)(currentGen ^ 1);

        // 2. Write nextGen's tracker chunks + main blob. These keys live in a
        //    namespace disjoint from currentGen's, so we never collide with
        //    the live data being served to readers.
        WriteLongMap(nextGen, SlotCountKind, snapshot.SlotCountByAddress);
        WriteIntMap(nextGen, CodeRefcountKind, snapshot.CodeHashRefcounts);
        WriteIntMap(nextGen, CodeSizeKind, snapshot.CodeHashSizes);

        WriteMainBlob(nextGen, snapshot);

        // 3. ATOMIC COMMIT — Put on LatestKey is single-key, RocksDB guarantees
        //    WAL atomicity per Put. After this returns, readers see nextGen.
        Span<byte> latestValue = stackalloc byte[LatestValueLength];
        latestValue[0] = nextGen;
        BinaryPrimitives.WriteInt64BigEndian(latestValue[1..], snapshot.BlockNumber);
        _db.PutSpan(LatestKey, latestValue);

        // 4. Delete currentGen. First-write case is a no-op (no prior commit).
        RemoveGeneration(currentGen);
    }

    public StateCompositionSnapshot? ReadSnapshot(byte gen)
    {
        Span<byte> mainKey = stackalloc byte[MainBlobKeyLength];
        mainKey[0] = gen;
        mainKey[1] = MainBlobMarker;

        byte[]? data = _db.Get(mainKey);
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
                _logger.Warn($"StateComposition: persisted snapshot in gen {gen} could not be decoded " +
                             $"(reason: {ex.Message}). Falling back to a fresh scan to rebuild the cached baseline.");
            return null;
        }

        Dictionary<ValueHash256, long> slotCounts = ReadLongMap(gen, SlotCountKind);
        Dictionary<ValueHash256, int> codeRefcounts = ReadIntMap(gen, CodeRefcountKind);
        Dictionary<ValueHash256, int> codeSizes = ReadIntMap(gen, CodeSizeKind);

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
        if (latestBytes is null || latestBytes.Length != LatestValueLength) return null;

        byte gen = latestBytes[0];
        if (gen > 1)
        {
            if (_logger.IsWarn)
                _logger.Warn($"StateComposition: LatestKey gen byte {gen} out of range; treating as no snapshot.");
            return null;
        }

        return ReadSnapshot(gen);
    }

    /// <summary>
    /// Boot-time cleanup. Removes orphan keys in the non-current generation
    /// left by a crash mid-WriteSnapshot. Safe to call on a fresh DB.
    /// </summary>
    public void PurgeOldEntries()
    {
        byte[]? latestBytes = _db.Get(LatestKey);
        if (latestBytes is null || latestBytes.Length != LatestValueLength) return;

        byte currentGen = latestBytes[0];
        byte otherGen = (byte)(currentGen ^ 1);
        RemoveGeneration(otherGen);
    }

    private byte ReadCurrentGen()
    {
        byte[]? latestBytes = _db.Get(LatestKey);
        if (latestBytes is null || latestBytes.Length != LatestValueLength) return 0;
        byte gen = latestBytes[0];
        return gen <= 1 ? gen : (byte)0;
    }

    private void RemoveGeneration(byte gen)
    {
        // Main blob.
        Span<byte> mainKey = stackalloc byte[MainBlobKeyLength];
        mainKey[0] = gen;
        mainKey[1] = MainBlobMarker;
        _db.Remove(mainKey);

        // Chunks. We don't know the exact count, so iterate per kind until a
        // Get returns null. With stable-prefix overwrite-in-place, this is
        // bounded by the live tracker-map chunk count (~150 per kind at
        // mainnet scale, ~450 total) — cheap compared to a GetAllKeys scan.
        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        chunkKey[0] = gen;
        foreach (byte kind in (ReadOnlySpan<byte>)[SlotCountKind, CodeRefcountKind, CodeSizeKind])
        {
            chunkKey[1] = kind;
            int chunkIdx = 0;
            while (true)
            {
                BinaryPrimitives.WriteInt32BigEndian(chunkKey[2..6], chunkIdx);
                if (_db.Get(chunkKey) is null) break;
                _db.Remove(chunkKey);
                chunkIdx++;
            }
        }
    }

    private void WriteMainBlob(byte gen, StateCompositionSnapshot snapshot)
    {
        Span<byte> mainKey = stackalloc byte[MainBlobKeyLength];
        mainKey[0] = gen;
        mainKey[1] = MainBlobMarker;

        int length = Decoder.GetLength(snapshot);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            RlpStream stream = new(buffer);
            Decoder.Encode(stream, snapshot);
            _db.PutSpan(mainKey, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void WriteLongMap(byte gen, byte kind, IReadOnlyDictionary<ValueHash256, long> map)
    {
        if (map.Count == 0) return;

        const int entrySize = 32 + 8;
        using IEnumerator<KeyValuePair<ValueHash256, long>> entries = map.GetEnumerator();
        WriteMap(gen, kind, map.Count, entries, entrySize, static (kvp, dst) =>
        {
            kvp.Key.Bytes.CopyTo(dst);
            BinaryPrimitives.WriteInt64BigEndian(dst[32..40], kvp.Value);
        });
    }

    private void WriteIntMap(byte gen, byte kind, IReadOnlyDictionary<ValueHash256, int> map)
    {
        if (map.Count == 0) return;

        const int entrySize = 32 + 4;
        using IEnumerator<KeyValuePair<ValueHash256, int>> entries = map.GetEnumerator();
        WriteMap(gen, kind, map.Count, entries, entrySize, static (kvp, dst) =>
        {
            kvp.Key.Bytes.CopyTo(dst);
            BinaryPrimitives.WriteInt32BigEndian(dst[32..36], kvp.Value);
        });
    }

    private delegate void EntryWriter<TValue>(KeyValuePair<ValueHash256, TValue> kvp, Span<byte> dst);

    private void WriteMap<TValue>(
        byte gen,
        byte kind,
        int count,
        IEnumerator<KeyValuePair<ValueHash256, TValue>> entries,
        int entrySize,
        EntryWriter<TValue> writeEntry)
    {
        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        chunkKey[0] = gen;
        chunkKey[1] = kind;

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
                BinaryPrimitives.WriteInt32BigEndian(chunkKey[2..6], chunkIdx);
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

    private Dictionary<ValueHash256, long> ReadLongMap(byte gen, byte kind)
    {
        Dictionary<ValueHash256, long> result = [];
        ReadMap(gen, kind, 32 + 8, (entry, dict) =>
        {
            ValueHash256 hash = new(entry[..32]);
            long value = BinaryPrimitives.ReadInt64BigEndian(entry[32..40]);
            dict[hash] = value;
        }, result);
        return result;
    }

    private Dictionary<ValueHash256, int> ReadIntMap(byte gen, byte kind)
    {
        Dictionary<ValueHash256, int> result = [];
        ReadMap(gen, kind, 32 + 4, (entry, dict) =>
        {
            ValueHash256 hash = new(entry[..32]);
            int value = BinaryPrimitives.ReadInt32BigEndian(entry[32..36]);
            dict[hash] = value;
        }, result);
        return result;
    }

    private void ReadMap<TDict>(
        byte gen,
        byte kind,
        int entrySize,
        EntryReader<TDict> readEntry,
        TDict dict)
    {
        Span<byte> chunkKey = stackalloc byte[ChunkKeyLength];
        chunkKey[0] = gen;
        chunkKey[1] = kind;
        int chunkIdx = 0;
        while (true)
        {
            BinaryPrimitives.WriteInt32BigEndian(chunkKey[2..6], chunkIdx);
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
                LogChunkTruncated(gen, kind, chunkIdx);
                break;
            }
            int chunkCount = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(0, 4));
            if (chunkCount < 0 || data.Length < 4 + (long)chunkCount * entrySize)
            {
                LogChunkTruncated(gen, kind, chunkIdx);
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

    private void LogChunkTruncated(byte gen, byte kind, int chunkIdx)
    {
        if (chunkIdx > 0 && _logger.IsWarn)
            _logger.Warn($"StateComposition: corrupt chunk {chunkIdx} for gen {gen} kind {kind:x2}; " +
                         $"loaded {chunkIdx} valid chunk(s) before truncation. Plugin will rescan.");
    }
}
