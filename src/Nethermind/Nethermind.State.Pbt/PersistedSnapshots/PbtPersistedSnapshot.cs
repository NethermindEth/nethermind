// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Memory;
using Nethermind.Core.Utils;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Persistence;

namespace Nethermind.State.Pbt.PersistedSnapshots;

public sealed class PbtPersistedSnapshot : SmallRefCountingDisposable
{
    private readonly ArenaReservation _reservation;
    private readonly BlobArenaManager _blobs;
    private readonly IRefCountingMemoryProvider _memory;
    private readonly RefCountedBloomFilter _bloom;
    private readonly List<BlobArenaFile> _files = [];
    private int _released;
    public StateId From { get; }
    public StateId To { get; }
    public ValueHash256 TreeRoot { get; }
    public SnapshotTier Tier { get; }
    public SnapshotLocation Location { get; }
    /// <summary>The block span from <see cref="From"/> to <see cref="To"/>; with <see cref="To"/>, the snapshot's catalog key.</summary>
    public long Depth => unchecked((long)(To.BlockNumber - From.BlockNumber));
    public long Size => _reservation.Size;
    public RefCountedBloomFilter BloomRef => _bloom;
    public ArenaReservation Reservation => _reservation;

    /// <remarks>
    /// Borrows the reservation and bloom, acquiring independent references along with one lease per
    /// referenced blob file. Construction failure releases only those newly acquired references.
    /// </remarks>
    public PbtPersistedSnapshot(CatalogEntry entry, ArenaReservation reservation, BlobArenaManager blobs,
        IRefCountingMemoryProvider nodeGroupMemory, RefCountedBloomFilter bloom)
    {
        _reservation = reservation;
        _blobs = blobs;
        _memory = nodeGroupMemory;
        _bloom = bloom;
        From = entry.From;
        To = entry.To;
        Tier = entry.Tier;
        Location = entry.Location;
        reservation.AcquireLease();
        bool bloomLeased = false;
        try
        {
            bloom.AcquireLease();
            bloomLeased = true;
            if (!entry.Tier.IsPersisted() || reservation.Size != entry.Location.Size || reservation.Offset != entry.Location.Offset)
                throw new InvalidDataException("Invalid persisted PBT catalog entry.");
            ArenaByteReader reader = reservation.CreateReader();
            TreeRoot = PbtPersistedFormat.ReadTreeRoot(reader);
            using SortedTableEnumerator<ArenaByteReader, NoOpPin> owners = new(reader, new(0, reader.Length));
            while (owners.MoveNext(reader) && owners.CurrentKey[0] == PbtPersistedKey.Ownership)
            {
                ushort id = BinaryPrimitives.ReadUInt16BigEndian(owners.CurrentKey[1..]);
                if (!blobs.TryLeaseFile(id, out BlobArenaFile? file)) throw new InvalidOperationException($"Blob arena {id} not registered with the blob manager");
                _files.Add(file);
            }
        }
        catch
        {
            foreach (BlobArenaFile file in _files) file.Dispose();
            if (bloomLeased) bloom.Dispose();
            reservation.Dispose();
            throw;
        }
    }

    private PbtPersistedSnapshot(PbtPersistedSnapshot source, RefCountedBloomFilter bloom)
    {
        _reservation = source._reservation;
        _blobs = source._blobs;
        _memory = source._memory;
        _bloom = bloom;
        From = source.From;
        To = source.To;
        TreeRoot = source.TreeRoot;
        Tier = source.Tier;
        Location = source.Location;
        _reservation.AcquireLease();
        bool bloomLeased = false;
        try
        {
            bloom.AcquireLease();
            bloomLeased = true;
            _files.Capacity = source._files.Count;
            foreach (BlobArenaFile file in source._files)
            {
                if (!_blobs.TryLeaseFile(file.BlobArenaId, out BlobArenaFile? leased))
                    throw new InvalidOperationException("Persisted PBT blob lease disappeared while rebinding its bloom.");
                _files.Add(leased);
            }
        }
        catch
        {
            foreach (BlobArenaFile file in _files) file.Dispose();
            if (bloomLeased) bloom.Dispose();
            _reservation.Dispose();
            throw;
        }
    }

    public PbtPersistedSnapshot WithBloom(RefCountedBloomFilter bloom)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return new(this, bloom);
    }

    public bool TryLease() => TryAcquireLease();
    public WholeReadSession BeginWholeReadSession()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return _reservation.BeginWholeReadSession();
    }
    public PbtPersistedScanner Scan() => new(this);

    /// <summary>The bloom hash of every entity this snapshot holds.</summary>
    public IEnumerable<ulong> EntityBloomHashes()
    {
        using PbtPersistedScanner scanner = Scan();
        while (scanner.MoveNext())
            if (PbtPersistedKey.IsEntity(scanner.Key)) yield return PbtPersistedKey.BloomHash(scanner.Key);
    }

    public bool TryGetAccount(in ValueHash256 addressHash, out PbtAccount? account)
    {
        bool found = TryReadEntity(PbtPersistedKey.AddressEntity(addressHash, PbtPersistedKey.Account), out byte[]? payload);
        account = payload is null ? null : PbtAccount.Decode(payload);
        return found;
    }

    /// <summary>Returns a caller-owned decoded run, or false when this layer has no descriptor.</summary>
    /// <remarks>Return nonempty runs through SlotRun.Return. A present empty run is not a miss.</remarks>
    public bool TryGetSlotRun<TKey>(in TKey runKey, out PackedSlotRun? run) where TKey : struct, IPbtKey<TKey>
    {
        bool found = TryReadEntity(PbtPersistedKey.Run(runKey), out byte[]? payload);
        run = !found ? null : DecodeRun(payload!);
        return found;
    }

    public bool TryGetCode(in ValueHash256 codeHash, out CodeInfo? code)
    {
        bool found = TryReadEntity(PbtPersistedKey.CodeEntity(codeHash), out byte[]? payload);
        code = found ? new CodeInfo(payload!) : null;
        return found;
    }

    public bool TryGetStorageClear(in ValueHash256 addressHash) =>
        TryReadEntity(PbtPersistedKey.AddressEntity(addressHash, PbtPersistedKey.Clear), out _);

    /// <summary>Returns one caller-owned group reference, a found null tombstone, or an absent descriptor.</summary>
    public bool TryGetNodeGroup<TPath>(in TPath path, out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        bool found = TryReadEntity(PbtPersistedKey.Group(path), out byte[]? bytes);
        payload = bytes is null ? null : DecodeGroup(path, bytes);
        return found;
    }

    public void PersistOnShutdown()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        _reservation.PersistOnShutdown();
        foreach (BlobArenaFile file in _files) file.PersistOnShutdown();
    }

    protected override void CleanUp()
    {
        Volatile.Write(ref _released, 1);
        _bloom.Dispose();
        foreach (BlobArenaFile file in _files)
        {
            file.Dispose();
            if (file.HasOnlyManagerLease) _blobs.TryResetOrphanedFrontier(file);
        }
        _reservation.Dispose();
    }

    private bool TryReadEntity(ReadOnlySpan<byte> key, out byte[]? payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        if (!_bloom.Filter.MightContain(PbtPersistedKey.BloomHash(key)))
        {
            payload = null;
            return false;
        }
        ArenaByteReader reader = _reservation.CreateReader();
        if (!SortedTableReader.TrySeek<ArenaByteReader, NoOpPin>(reader, new(0, reader.Length), key, out Bound value))
        {
            payload = null;
            return false;
        }
        payload = ReadPayload(key, value);
        return true;
    }

    public byte[]? ReadPayload(ReadOnlySpan<byte> key, Bound bound)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        ArenaByteReader reader = _reservation.CreateReader();
        byte[] value = ReadValue(reader, bound);
        int length = PbtPersistedFormat.PayloadLength(key, value, out uint chunks);
        if (length < 0) return null;
        if (chunks == 0) return value[1..];
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        Span<byte> referenceBytes = stackalloc byte[NodeRef.Size];
        for (uint i = 0; i < chunks; i++)
        {
            byte[] chunkKey = PbtPersistedKey.Chunk(key, i);
            if (!SortedTableReader.TrySeek<ArenaByteReader, NoOpPin>(reader, new(0, reader.Length), chunkKey, out Bound referenceBound)
                || referenceBound.Length != NodeRef.Size)
                throw new InvalidDataException("Missing persisted PBT payload chunk.");
            reader.TryRead(referenceBound.Offset, referenceBytes);
            NodeRef reference = NodeRef.Read(referenceBytes);
            int offset = checked((int)((long)i * PbtPersistedFormat.ChunkSize));
            ReadChunk(reference, payload.AsSpan(offset, Math.Min(PbtPersistedFormat.ChunkSize, length - offset)));
        }
        return payload;
    }

    public void ApplyTo(IPbtPersistence.IWriteBatch batch, CancellationToken cancellationToken)
    {
        using PbtPersistedScanner scanner = Scan();
        while (scanner.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = scanner.Key;
            if (!PbtPersistedKey.IsEntity(key)) continue;
            byte[]? bytes = scanner.ReadPayload();
            switch (key[0])
            {
                case PbtPersistedKey.Address:
                    ValueHash256 address = new(key.Slice(1, 32));
                    switch (key[33])
                    {
                        case PbtPersistedKey.Account: batch.SetAccount(address, bytes is null ? null : PbtAccount.Decode(bytes)); break;
                        case PbtPersistedKey.HeaderRun:
                            PackedSlotRun headerRun = DecodeRun(bytes!);
                            try { batch.SetSlotRun(PbtPersistedKey.DecodeHeaderRun(key), headerRun); }
                            finally { SlotRun.Return(headerRun); }
                            break;
                        case PbtPersistedKey.StorageRun:
                            PackedSlotRun storageRun = DecodeRun(bytes!);
                            try { batch.SetSlotRun(PbtPersistedKey.DecodeStorageRun(key), storageRun); }
                            finally { SlotRun.Return(storageRun); }
                            break;
                    }
                    break;
                case PbtPersistedKey.Code: batch.SetCode(new ValueHash256(key.Slice(1, 32)), new CodeInfo(bytes!)); break;
                default:
                    PbtStorageNodePath path = PbtPersistedKey.DecodeGroup(key);
                    using (RefCountingMemory? payload = bytes is null ? null : DecodeGroup(path, bytes)) batch.SetNodeGroup(path, payload);
                    break;
            }
        }
    }

    private RefCountingMemory DecodeGroup<TPath>(in TPath path, ReadOnlySpan<byte> bytes) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtNodeGroupCodec.ValidateNodes(path, bytes);
        RefCountingMemory payload = _memory.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        return payload;
    }

    private static PackedSlotRun DecodeRun(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? SlotRun.Empty : SlotRun.Decode(bytes);

    private static byte[] ReadValue(scoped in ArenaByteReader reader, Bound bound)
    {
        if (bound.Length is < 0 or > 255) throw new InvalidDataException("Invalid persisted PBT table value length.");
        byte[] value = new byte[(int)bound.Length];
        reader.TryRead(bound.Offset, value);
        return value;
    }

    private long ChunkBounds(in NodeRef reference, int expectedLength)
    {
        BlobArenaFile file = _blobs.GetFile(reference.BlobArenaId);
        if (reference.RlpDataOffset < 0 || reference.RlpDataOffset >= file.Frontier) throw new InvalidDataException("Persisted PBT blob reference out of bounds.");
        Span<byte> header = stackalloc byte[4];
        if (file.RandomRead(reference.RlpDataOffset, header[..1]) != 1) throw new InvalidDataException("Truncated persisted PBT blob header.");
        int prefix = header[0], headerSize = 1, length;
        if (prefix < 0x80) { length = 1; headerSize = 0; }
        else if (prefix <= 0xB7)
        {
            length = prefix - 0x80;
            if (length == 1)
            {
                if (file.RandomRead((long)reference.RlpDataOffset + 1, header.Slice(1, 1)) != 1 || header[1] < 0x80)
                    throw new InvalidDataException("Noncanonical persisted PBT RLP byte.");
            }
        }
        else if (prefix is >= 0xB8 and <= 0xBA)
        {
            int width = prefix - 0xB7;
            headerSize += width;
            if (file.RandomRead((long)reference.RlpDataOffset + 1, header.Slice(1, width)) != width || header[1] == 0)
                throw new InvalidDataException("Invalid persisted PBT RLP length.");
            length = 0;
            for (int i = 0; i < width; i++) length = (length << 8) | header[1 + i];
            if (length <= 55) throw new InvalidDataException("Noncanonical persisted PBT RLP length.");
        }
        else throw new InvalidDataException("Persisted PBT blob must be a bounded RLP string.");
        if (length != expectedLength || length > PbtPersistedFormat.ChunkSize || (long)reference.RlpDataOffset + headerSize + length > file.Frontier)
            throw new InvalidDataException("Persisted PBT blob length mismatch.");
        return (long)reference.RlpDataOffset + headerSize;
    }

    private void ReadChunk(in NodeRef reference, Span<byte> destination)
    {
        long offset = ChunkBounds(reference, destination.Length);
        if (_blobs.GetFile(reference.BlobArenaId).RandomRead(offset, destination) != destination.Length)
            throw new InvalidDataException("Truncated persisted PBT payload.");
    }
}

public sealed class PbtPersistedScanner : IDisposable
{
    private readonly PbtPersistedSnapshot _snapshot;
    private SortedTableEnumerator<ArenaByteReader, NoOpPin> _scanner;
    private bool _disposed;

    public PbtPersistedScanner(PbtPersistedSnapshot snapshot)
    {
        if (!snapshot.TryLease()) throw new ObjectDisposedException(nameof(snapshot));
        _snapshot = snapshot;
        try
        {
            ArenaByteReader reader = snapshot.Reservation.CreateReader();
            _scanner = new(reader, new(0, reader.Length));
        }
        catch { snapshot.Dispose(); throw; }
    }

    public bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArenaByteReader reader = _snapshot.Reservation.CreateReader();
        return _scanner.MoveNext(reader);
    }
    public ReadOnlySpan<byte> Key => _scanner.CurrentKey;
    private Bound Value => _scanner.CurrentValue;
    public byte[]? ReadPayload() => _snapshot.ReadPayload(Key, Value);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.Dispose();
        _snapshot.Dispose();
    }
}
