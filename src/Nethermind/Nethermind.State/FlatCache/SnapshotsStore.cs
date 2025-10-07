// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Prometheus;

namespace Nethermind.State.FlatCache;

public class SnapshotsStore
{
    private readonly ISortedKeyValueStore _snapshotStore;
    private readonly ILogger _logger;

    public static Counter _snapshotTimes =
        Prometheus.Metrics.CreateCounter("snapshotstore_times", "Total number of snapshots", "part");
    private readonly IJsonSerializer _jsonSerialiver;

    public SnapshotsStore(
        [KeyFilter(DbNames.FlatCacheLog)] IDb snapshotStore,
        IJsonSerializer jsonSerializer,
        ILogManager logManager)
    {
        if (snapshotStore is not ISortedKeyValueStore sorted) throw new InvalidOperationException($"snapshot store must be a {nameof(ISortedKeyValueStore)}");

        _jsonSerialiver = jsonSerializer;
        _logger = logManager.GetClassLogger<SnapshotsStore>();
        _snapshotStore = sorted;
    }

    public bool TryGetValue(StateId current, out Snapshot value)
    {
        long sw = Stopwatch.GetTimestamp();
        try
        {
            byte[]? snapshotBytes = _snapshotStore.Get(EncodeKey(current));
            if (snapshotBytes is null)
            {
                value = default;
                return false;
            }
            _snapshotTimes.WithLabels("get_value_read").Inc(Stopwatch.GetTimestamp() - sw);
            sw = Stopwatch.GetTimestamp();

            value = DecodeSnapshot(snapshotBytes);
            return true;
        }
        finally
        {
            _snapshotTimes.WithLabels("get_value").Inc(Stopwatch.GetTimestamp() - sw);
        }
    }

    public void AddBlock(StateId endBlock, Snapshot snapshot)
    {
        long sw = Stopwatch.GetTimestamp();
        try
        {
            byte[] serialized = EncodeSnapshot(snapshot);
            if (_logger.IsDebug) _logger.Debug($"Add block {endBlock}, {serialized is not null}");
            _snapshotStore[EncodeKey(endBlock)] = serialized;
        }
        finally
        {
            _snapshotTimes.WithLabels("add_block").Inc(Stopwatch.GetTimestamp() - sw);
        }
    }

    public void Remove(StateId firstKey)
    {
        byte[] key = EncodeKey(firstKey);
        _snapshotStore.Remove(key);
    }

    public List<StateId> GetKeysBetween(long startingBlockNumber, long endingBlockNumber)
    {
        if (startingBlockNumber < 0) throw new InvalidOperationException("block number cannot be negative I'm afraid"); // Because of binary order
        long sw = Stopwatch.GetTimestamp();

        using var iterator = _snapshotStore.GetViewBetween(
            EncodeKey(new StateId(startingBlockNumber, ValueKeccak.Zero)),
            EncodeKey(new StateId(endingBlockNumber, ValueKeccak.Zero))
        );

        List<StateId> result = new List<StateId>();

        while (iterator.MoveNext())
        {
            result.Add(DecodeKey(iterator.CurrentKey));
        }

        _snapshotTimes.WithLabels("get_keys_after").Inc(Stopwatch.GetTimestamp() - sw);
        return result;
    }

    public StateId? GetLast()
    {
        var lastKey = _snapshotStore.LastKey;
        if (lastKey is null)
        {
            return default;
        }
        return DecodeKey(_snapshotStore.LastKey);
    }

    private StateId DecodeKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != 40) throw new InvalidDataException("Key must be 40 bytes");
        long blockNumber = BinaryPrimitives.ReadInt64BigEndian(key[..8]);
        ValueHash256 stateRoot = new ValueHash256(key[8..]);
        return new StateId(blockNumber, stateRoot);
    }

    private byte[] EncodeKey(StateId key)
    {
        byte[] keyBytes = new byte[40];
        BinaryPrimitives.WriteInt64BigEndian(keyBytes, key.blockNumber);
        key.stateRoot.Bytes.CopyTo(keyBytes.AsSpan()[8..]);
        return keyBytes;
    }

    private byte[] EncodeSnapshot(Snapshot snapshot)
    {
        MemoryStream stream = new MemoryStream();
        _jsonSerialiver.Serialize(stream, snapshot.ToSerializeSnapshot());
        return stream.ToArray();
    }

    private Snapshot DecodeSnapshot(byte[] bytes)
    {
        MemoryStream stream = new MemoryStream(bytes);
        var aha = _jsonSerialiver.Deserialize<LazySerializeSnapshot>(stream);
        return aha.GetSnapshot();
    }
}
