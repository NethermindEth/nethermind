// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using Autofac.Features.AttributeFilters;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

using Nethermind.StateComposition.Data;

namespace Nethermind.StateComposition.Snapshots;

/// <summary>
/// Persists <see cref="StateCompositionSnapshot"/> entries to a dedicated RocksDB database.
/// Keys are 8-byte big-endian block numbers for natural sort order.
/// A sentinel key stores the latest block number for O(1) lookup.
/// </summary>
internal sealed class StateCompositionSnapshotStore(
    [KeyFilter("stateComposition")] IDb db,
    ILogManager logManager)
{
    public const string DbName = "stateComposition";

    private static readonly StateCompositionSnapshotDecoder Decoder = StateCompositionSnapshotDecoder.Instance;

    // long.MaxValue big-endian: theoretical collision with a real block-number key
    // lands ~3.5 trillion years out at 12s blocks, so no schema migration needed.
    private static readonly byte[] LatestKey = [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
    private readonly ILogger _logger = logManager.GetClassLogger<StateCompositionSnapshotStore>();

    public void WriteSnapshot(StateCompositionSnapshot snapshot)
    {
        // Write order: put new → update LatestKey → remove old. A crash after the
        // put but before the LatestKey update leaves the old entry reachable via
        // LatestKey, so the next boot's PurgeOldEntries keeps it and warm-restart
        // still works. The reverse order (remove-first) would let PurgeOldEntries
        // delete the freshly written snapshot because LatestKey still pointed at
        // the already-removed old block.
        long prevBlock = long.MinValue;
        byte[]? prevBytes = db.Get(LatestKey);
        if (prevBytes is not null && prevBytes.Length >= 8)
            prevBlock = BinaryPrimitives.ReadInt64BigEndian(prevBytes);

        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, snapshot.BlockNumber);

        int length = Decoder.GetLength(snapshot);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            RlpStream stream = new(buffer);
            Decoder.Encode(stream, snapshot);
            db.PutSpan(key, buffer.AsSpan(0, length));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        Span<byte> blockBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(blockBytes, snapshot.BlockNumber);
        db.PutSpan(LatestKey, blockBytes);

        if (prevBlock != long.MinValue && prevBlock != snapshot.BlockNumber)
        {
            Span<byte> prevKey = stackalloc byte[8];
            BinaryPrimitives.WriteInt64BigEndian(prevKey, prevBlock);
            db.Remove(prevKey);
        }
    }

    public StateCompositionSnapshot? ReadSnapshot(long blockNumber)
    {
        Span<byte> key = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(key, blockNumber);

        byte[]? data = db.Get(key);
        if (data is null) return null;

        // Legacy snapshots from earlier schema versions will mis-align the decoder
        // and throw. Log once and treat as "no snapshot" so the plugin falls back
        // to a fresh scan — callers don't need to distinguish "missing" from "corrupt".
        try
        {
            Rlp.ValueDecoderContext ctx = data.AsRlpValueContext();
            return Decoder.Decode(ref ctx);
        }
        catch (Exception ex) when (ex is RlpException or InvalidDataException or EndOfStreamException or IOException)
        {
            if (_logger.IsWarn)
                _logger.Warn($"StateComposition: persisted snapshot at block {blockNumber} could not be decoded " +
                             $"(reason: {ex.Message}). Falling back to a fresh scan to rebuild the cached baseline.");
            return null;
        }
    }

    public StateCompositionSnapshot? ReadLatestSnapshot()
    {
        byte[]? latestBytes = db.Get(LatestKey);
        if (latestBytes is null || latestBytes.Length < 8) return null;

        long latestBlock = BinaryPrimitives.ReadInt64BigEndian(latestBytes);
        return ReadSnapshot(latestBlock);
    }

    public void PurgeOldEntries()
    {
        byte[]? latestBytes = db.Get(LatestKey);
        long latestBlock = -1;
        if (latestBytes is not null && latestBytes.Length >= 8)
            latestBlock = BinaryPrimitives.ReadInt64BigEndian(latestBytes);

        Span<byte> latestKey = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(latestKey, latestBlock);

        int removed = 0;
        foreach (byte[] key in db.GetAllKeys())
        {
            if (key.AsSpan().SequenceEqual(LatestKey)) continue;
            if (latestBlock >= 0 && key.AsSpan().SequenceEqual(latestKey)) continue;

            db.Remove(key);
            removed++;
        }

        if (removed > 0 && _logger.IsInfo)
            _logger.Info($"StateComposition: purged {removed} old snapshot entries");
    }
}
