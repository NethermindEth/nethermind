// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Org.BouncyCastle.Crypto.EC;
using Prometheus;

namespace Nethermind.State.FlatCache;

public class PersistedBigCache : IBigCache
{
    private byte[] _currentBlockNumberKey = Bytes.FromHexString("00000000");
    private byte[] _snapshotCountNumberKey = Bytes.FromHexString("0000000f");

    AccountDecoder _accountDecoder = AccountDecoder.Instance;

    public PersistedBigCache([KeyFilter(DbNames.FlatCache)] IDb db)
    {
        _db = db;

        byte[]? currentBlockNumberBytes = db[_currentBlockNumberKey];
        if (currentBlockNumberBytes is not null)
            _currentBlockNumber = BinaryPrimitives.ReadInt64BigEndian(currentBlockNumberBytes);

        byte[]? snapshotCountBytes = db[_snapshotCountNumberKey];
        if (snapshotCountBytes is not null)
            _snapshotCount = BinaryPrimitives.ReadInt64BigEndian(snapshotCountBytes);
    }

    private long _currentBlockNumber = -1;
    public long CurrentBlockNumber => _currentBlockNumber;

    private long _snapshotCount = 0;
    private readonly IDb _db;
    public long SnapshotCount => _snapshotCount;

    public bool TryGetValue(Address address, out Account? acc)
    {
        Span<byte> key = stackalloc byte[21];
        Span<byte> value = _db.GetSpan(EncodeAccountKey(address, key));
        try
        {
            if (value.IsEmpty || value.IsNull())
            {
                acc = null;
                return false;
            }

            try
            {
                Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(value[8..]);
                acc = _accountDecoder.Decode(ref ctx);
            }
            catch (Exception)
            {
                Console.Error.WriteLine($"Failed to decode account, {value[8..].ToHexString()}");
                throw;
            }
            return true;
        }
        finally
        {
            _db.DangerousReleaseMemory(value);
        }
    }

    public IBigCache.IStorageReader GetStorageReader(Address address)
    {
        Span<byte> key = stackalloc byte[21];
        Span<byte> selfDestructBytes = _db.GetSpan(EncodeAccountSelfDestructKey(address, key));
        long selfDestructBlockNumber = 0;
        try
        {
            if (selfDestructBytes.IsEmpty || selfDestructBytes.IsNull())
            {
            }
            else
            {
                selfDestructBlockNumber = BinaryPrimitives.ReadInt64BigEndian(selfDestructBytes);
            }
        }
        finally
        {
            _db.DangerousReleaseMemory(selfDestructBytes);
        }

        return new PersistentBigCacheStorageReader(_db, address, selfDestructBlockNumber);
    }

    public void Subtract(StateId firstKey, Snapshot snapshot)
    {
    }

    private Gauge _snapshotCountMetric = Prometheus.Metrics.CreateGauge("flatcache_bigcache_snapshot_count", "snapshot count");
    public void Add(StateId pickedSnapshot, Snapshot knownState)
    {
        long blockNumber = pickedSnapshot.blockNumber;
        Span<byte> keyBuffer = stackalloc byte[53];
        byte[] rlpBuffer = new byte[1000];
        Span<byte> rlpBufferSpan = rlpBuffer;

        using (IWriteBatch writeBatch = _db.StartWriteBatch())
        {
            foreach (var knownStateAccount in knownState.Accounts)
            {
                int accountRlpLength = _accountDecoder.GetLength(knownStateAccount.Value);
                Span<byte> valueBuffer = rlpBuffer[..(accountRlpLength + 8)];
                BinaryPrimitives.WriteInt64BigEndian(valueBuffer, blockNumber);
                using var stream = _accountDecoder.EncodeToNewNettyStream(knownStateAccount.Value);
                stream.AsSpan().CopyTo(valueBuffer[8..]);

                writeBatch.PutSpan(EncodeAccountKey(knownStateAccount.Key, keyBuffer), valueBuffer);
            }

            foreach (var knownStateStorage in knownState.Storages)
            {
                Address addr = knownStateStorage.Key;
                if (knownStateStorage.Value.HasSelfDestruct)
                {
                    // Self destruct first. Concurrent reader will just drop existing entries with block number lower than selfdestruct
                    BinaryPrimitives.WriteInt64BigEndian(rlpBufferSpan, blockNumber);
                    writeBatch.PutSpan(EncodeAccountSelfDestructKey(addr, keyBuffer), rlpBufferSpan[..8]);
                }

                foreach (var kv in knownStateStorage.Value.Slots)
                {
                    BinaryPrimitives.WriteInt64BigEndian(rlpBufferSpan, blockNumber);

                    byte[] encodedBytes = Rlp.Encode(kv.Value).Bytes;
                    encodedBytes.CopyTo(rlpBufferSpan[8..]);

                    writeBatch.PutSpan(EncodeSlotKey(addr, kv.Key, keyBuffer), rlpBufferSpan[..(8+encodedBytes.Length)]);
                }
            }

            _currentBlockNumber = blockNumber;
            BinaryPrimitives.WriteInt64BigEndian(rlpBufferSpan, _currentBlockNumber);
            writeBatch.PutSpan(_currentBlockNumberKey, rlpBufferSpan[..8]);

            _snapshotCount++;
            BinaryPrimitives.WriteInt64BigEndian(rlpBufferSpan, _snapshotCount);
            writeBatch.PutSpan(_snapshotCountNumberKey, rlpBufferSpan[..8]);
        }
        _snapshotCountMetric.Set(SnapshotCount);
    }

    private Span<byte> EncodeAccountKey(Address address, Span<byte> key)
    {
        if (key.Length < 21) throw new InvalidOperationException("Key length must be at least 21");
        address.Bytes.CopyTo(key);
        key[20] = 0;
        return key[..21];
    }

    private Span<byte> EncodeAccountSelfDestructKey(Address address, Span<byte> key)
    {
        if (key.Length < 21) throw new InvalidOperationException("Key length must be at least 21");
        address.Bytes.CopyTo(key);
        key[20] = 1;
        return key[..21];
    }

    private static Span<byte> EncodeSlotKey(Address address, in UInt256 index, Span<byte> key)
    {
        if (key.Length < 21) throw new InvalidOperationException("Key length must be at least 53");
        address.Bytes.CopyTo(key);
        key[20] = 2;
        index.ToBigEndian(key[21..]);
        return key[..53];
    }

    private class PersistentBigCacheStorageReader(IDb db, Address address, long selfDestructBlockNumber): IBigCache.IStorageReader
    {
        public bool TryGetValue(in UInt256 index, out byte[]? value)
        {
            Span<byte> key = stackalloc byte[53];
            key = EncodeSlotKey(address, index, key);

            Span<byte> bytes = db.GetSpan(key);
            try
            {
                if (bytes.IsEmpty || bytes.IsNull())
                {
                    value = null;
                    return false;
                }

                long blockNumber = BinaryPrimitives.ReadInt64BigEndian(bytes);
                if (blockNumber < selfDestructBlockNumber)
                {
                    value = null;
                    return false;
                }

                Span<byte> remainingBytes = bytes[8..];
                Rlp.ValueDecoderContext ctx = new Rlp.ValueDecoderContext(remainingBytes);
                value = ctx.DecodeByteArray();

                return true;
            }
            finally
            {
                db.DangerousReleaseMemory(bytes);
            }
        }
    }
}
