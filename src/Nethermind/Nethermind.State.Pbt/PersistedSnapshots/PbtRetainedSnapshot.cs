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

internal sealed class PbtRetainedSnapshot : SmallRefCountingDisposable
{
    private readonly ArenaReservation _reservation;
    private readonly BlobArenaManager _blobs;
    private readonly IRefCountingMemoryProvider _memory;
    private readonly RefCountedBloomFilter _bloom;
    private readonly List<BlobArenaFile> _files = [];
    private int _released;
    internal StateId From { get; }
    internal StateId To { get; }
    internal ValueHash256 TreeRoot { get; }
    internal SnapshotTier Tier { get; }
    internal SnapshotLocation Location { get; }
    internal long Size => _reservation.Size;
    internal RefCountedBloomFilter BloomRef => _bloom;
    internal ArenaReservation Reservation => _reservation;

    /// <remarks>
    /// Borrows the reservation and bloom, acquiring independent references along with one lease per
    /// referenced blob file. Construction failure releases only those newly acquired references.
    /// </remarks>
    internal PbtRetainedSnapshot(CatalogEntry entry, ArenaReservation reservation, BlobArenaManager blobs,
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
                throw new InvalidDataException("Invalid retained PBT catalog entry.");
            ArenaByteReader reader = reservation.CreateReader();
            PbtRetainedTableValidation.Validate(reader);
            (PbtRetainedMetadata metadata, SortedSet<ushort> owners) = ValidateEntities(reader);
            if (metadata.From != From || metadata.To != To) throw new InvalidDataException("Retained PBT catalog/table identity mismatch.");
            TreeRoot = metadata.TreeRoot;
            _files.Capacity = owners.Count;
            foreach (ushort id in owners)
            {
                if (!blobs.TryLeaseFile(id, out BlobArenaFile? file)) throw new InvalidDataException($"Missing retained PBT blob arena {id}.");
                _files.Add(file);
            }
            ValidateBlobChunks(reader);
        }
        catch
        {
            foreach (BlobArenaFile file in _files) file.Dispose();
            if (bloomLeased) bloom.Dispose();
            reservation.Dispose();
            throw;
        }
    }

    private PbtRetainedSnapshot(PbtRetainedSnapshot source, RefCountedBloomFilter bloom)
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
                    throw new InvalidOperationException("Retained PBT blob lease disappeared while rebinding its bloom.");
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

    internal PbtRetainedSnapshot WithBloom(RefCountedBloomFilter bloom)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return new(this, bloom);
    }

    internal bool TryLease() => TryAcquireLease();
    internal WholeReadSession BeginWholeReadSession(bool adviseDontNeedOnDispose = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        return _reservation.BeginWholeReadSession(adviseDontNeedOnDispose);
    }
    internal PbtRetainedScanner Scan() => new(this);

    internal bool TryGetAccount(in ValueHash256 addressHash, out PbtAccount? account)
    {
        bool found = TryReadEntity(PbtRetainedKey.AddressEntity(addressHash, PbtRetainedKey.Account), out byte[]? payload);
        account = payload is null ? null : PbtAccount.Decode(payload);
        return found;
    }

    /// <summary>Returns a caller-owned decoded run, or false when this layer has no descriptor.</summary>
    /// <remarks>Return nonempty runs through SlotRun.Return. A present empty run is not a miss.</remarks>
    internal bool TryGetSlotRun<TKey>(in TKey runKey, out PackedSlotRun? run) where TKey : struct, IPbtKey<TKey>
    {
        bool found = TryReadEntity(PbtRetainedKey.Run(runKey), out byte[]? payload);
        run = !found ? null : DecodeRun(payload!);
        return found;
    }

    internal bool TryGetCode(in ValueHash256 codeHash, out CodeInfo? code)
    {
        bool found = TryReadEntity(PbtRetainedKey.CodeEntity(codeHash), out byte[]? payload);
        code = found ? new CodeInfo(payload!) : null;
        return found;
    }

    internal bool TryGetStorageClear(in ValueHash256 addressHash, out bool storedValue)
    {
        bool found = TryReadEntity(PbtRetainedKey.AddressEntity(addressHash, PbtRetainedKey.Clear), out byte[]? payload);
        storedValue = found && payload![0] != 0;
        return found;
    }

    /// <summary>Returns one caller-owned group reference, a found null tombstone, or an absent descriptor.</summary>
    internal bool TryGetNodeGroup<TPath>(in TPath path, out RefCountingMemory? payload) where TPath : struct, IPbtNodePath<TPath>
    {
        bool found = TryReadEntity(PbtRetainedKey.Group(path), out byte[]? bytes);
        payload = bytes is null ? null : DecodeGroup(path, bytes);
        return found;
    }

    internal void PersistOnShutdown()
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

    internal bool TryReadEntity(ReadOnlySpan<byte> key, out byte[]? payload)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        if (!_bloom.Filter.MightContain(PbtRetainedKey.BloomHash(key)))
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

    internal byte[]? ReadPayload(ReadOnlySpan<byte> key, Bound bound)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _released) != 0, this);
        ArenaByteReader reader = _reservation.CreateReader();
        byte[] value = ReadValue(reader, bound);
        int length = DescriptorLength(key, value, out uint chunks);
        if (length < 0) return null;
        if (chunks == 0) return value[1..];
        byte[] payload = GC.AllocateUninitializedArray<byte>(length);
        Span<byte> referenceBytes = stackalloc byte[NodeRef.Size];
        for (uint i = 0; i < chunks; i++)
        {
            byte[] chunkKey = PbtRetainedKey.Chunk(key, i);
            if (!SortedTableReader.TrySeek<ArenaByteReader, NoOpPin>(reader, new(0, reader.Length), chunkKey, out Bound referenceBound)
                || referenceBound.Length != NodeRef.Size)
                throw new InvalidDataException("Missing retained PBT payload chunk.");
            PbtRetainedTableValidation.Read(reader, referenceBound.Offset, referenceBytes);
            NodeRef reference = NodeRef.Read(referenceBytes);
            int offset = checked((int)((long)i * PbtRetainedSnapshotBuilder.ChunkSize));
            ReadChunk(reference, payload.AsSpan(offset, Math.Min(PbtRetainedSnapshotBuilder.ChunkSize, length - offset)));
        }
        return payload;
    }

    internal void ApplyTo(IPbtPersistence.IWriteBatch batch, CancellationToken cancellationToken)
    {
        using PbtRetainedScanner scanner = Scan();
        while (scanner.MoveNext())
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = scanner.Key;
            if (key[0] is 0 or PbtRetainedKey.Ownership || IsChunk(key)) continue;
            byte[]? bytes = scanner.ReadPayload();
            switch (key[0])
            {
                case PbtRetainedKey.Address:
                    ValueHash256 address = new(key.Slice(1, 32));
                    switch (key[33])
                    {
                        case PbtRetainedKey.Account: batch.SetAccount(address, bytes is null ? null : PbtAccount.Decode(bytes)); break;
                        case PbtRetainedKey.HeaderRun:
                            PackedSlotRun headerRun = DecodeRun(bytes!);
                            try { batch.SetSlotRun(PbtRetainedKey.DecodeHeaderRun(key), headerRun); }
                            finally { SlotRun.Return(headerRun); }
                            break;
                        case PbtRetainedKey.StorageRun:
                            PackedSlotRun storageRun = DecodeRun(bytes!);
                            try { batch.SetSlotRun(PbtRetainedKey.DecodeStorageRun(key), storageRun); }
                            finally { SlotRun.Return(storageRun); }
                            break;
                    }
                    break;
                case PbtRetainedKey.Code: batch.SetCode(new ValueHash256(key.Slice(1, 32)), new CodeInfo(bytes!)); break;
                default:
                    PbtStorageNodePath path = PbtRetainedKey.DecodeGroup(key);
                    using (RefCountingMemory? payload = bytes is null ? null : DecodeGroup(path, bytes)) batch.SetNodeGroup(path, payload);
                    break;
            }
        }
    }

    private RefCountingMemory DecodeGroup<TPath>(in TPath path, ReadOnlySpan<byte> bytes) where TPath : struct, IPbtNodePath<TPath>
    {
        PbtTraversalPath cursor = PbtTraversalPath.FromPath(stackalloc byte[PbtVariableTreeKey.MaxLength], path);
        PbtNodeGroupCodec.ValidateNodes(cursor, bytes);
        RefCountingMemory payload = _memory.Rent(bytes.Length);
        bytes.CopyTo(payload.GetSpan());
        return payload;
    }

    private static PackedSlotRun DecodeRun(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? SlotRun.Empty : SlotRunCodec.Decode(bytes);

    internal static bool IsChunk(ReadOnlySpan<byte> key)
    {
        if (key.Length < 2) return false;
        int descriptorLength = key[0] switch
        {
            PbtRetainedKey.Address when key.Length >= 34 => key[33] switch
            {
                PbtRetainedKey.Clear or PbtRetainedKey.Account => 35,
                PbtRetainedKey.HeaderRun => 36,
                PbtRetainedKey.StorageRun => 68,
                _ => 0,
            },
            PbtRetainedKey.Code => 34,
            PbtRetainedKey.AccountGroup or PbtRetainedKey.CodeGroup or PbtRetainedKey.StorageGroup when key.Length >= 3 => 4 + (BinaryPrimitives.ReadUInt16BigEndian(key[1..]) + 7) / 8,
            _ => 0,
        };
        return descriptorLength > 0 && key.Length == descriptorLength + 4 && key[descriptorLength - 1] == 1;
    }

    private static int DescriptorLength(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, out uint chunks)
    {
        chunks = 0;
        if (value.IsEmpty) throw new InvalidDataException("Empty retained PBT descriptor.");
        bool nullable = key[0] is PbtRetainedKey.AccountGroup or PbtRetainedKey.CodeGroup or PbtRetainedKey.StorageGroup
            || key[0] == PbtRetainedKey.Address && key[33] == PbtRetainedKey.Account;
        switch (value[0])
        {
            case 0 when nullable && value.Length == 1: return -1;
            case 1:
                int inline = value.Length - 1;
                ValidateLength(key, inline);
                if (key[0] == PbtRetainedKey.Address && key[33] == PbtRetainedKey.Clear && value[1] > 1)
                    throw new InvalidDataException("Invalid retained PBT clear value.");
                return inline;
            case 2 when value.Length == 9:
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(value[1..]);
                chunks = BinaryPrimitives.ReadUInt32LittleEndian(value[5..]);
                if (length <= PbtRetainedSnapshotBuilder.InlineLimit || length > int.MaxValue
                    || chunks != ((ulong)length + PbtRetainedSnapshotBuilder.ChunkSize - 1) / PbtRetainedSnapshotBuilder.ChunkSize)
                    throw new InvalidDataException("Invalid retained PBT chunk descriptor.");
                ValidateLength(key, (int)length);
                return (int)length;
            default: throw new InvalidDataException("Invalid retained PBT descriptor marker.");
        }
    }

    private static void ValidateLength(ReadOnlySpan<byte> key, int length)
    {
        bool valid = key[0] switch
        {
            PbtRetainedKey.Address => key[33] switch
            {
                PbtRetainedKey.Clear => length == 1,
                PbtRetainedKey.Account => length is 32 or 55 or 64,
                _ => length == 0 || length is >= 35 and <= 515 && (length - 3) % 32 == 0,
            },
            PbtRetainedKey.Code => true,
            _ => length is > 0 and <= PbtNodeGroupCodec.MaxPayloadLength,
        };
        if (!valid) throw new InvalidDataException("Invalid retained PBT entity payload length.");
    }

    private static byte[] ReadValue(scoped in ArenaByteReader reader, Bound bound)
    {
        if (bound.Length is < 0 or > 255) throw new InvalidDataException("Invalid retained PBT table value length.");
        byte[] value = new byte[(int)bound.Length];
        PbtRetainedTableValidation.Read(reader, bound.Offset, value);
        return value;
    }

    private static ReadOnlySpan<byte> ReadValueInto(scoped in ArenaByteReader reader, Bound bound, Span<byte> buffer)
    {
        if (bound.Length is < 0 or > 255) throw new InvalidDataException("Invalid retained PBT table value length.");
        Span<byte> value = buffer[..(int)bound.Length];
        PbtRetainedTableValidation.Read(reader, bound.Offset, value);
        return value;
    }

    private static (PbtRetainedMetadata, SortedSet<ushort>) ValidateEntities(scoped in ArenaByteReader reader)
    {
        StateId from = default, to = default;
        ValueHash256 root = default;
        int metadataMask = 0;
        SortedSet<ushort> referenced = [], owners = [];
        byte[]? active = null;
        uint expected = 0, count = 0;
        using SortedTableEnumerator<ArenaByteReader, NoOpPin> scanner = new(reader, new(0, reader.Length));
        Span<byte> valueBuffer = stackalloc byte[255];
        while (scanner.MoveNext(reader))
        {
            ReadOnlySpan<byte> key = scanner.CurrentKey;
            ReadOnlySpan<byte> value = ReadValueInto(reader, scanner.CurrentValue, valueBuffer);
            if (count != expected)
            {
                byte[] wanted = PbtRetainedKey.Chunk(active!, expected);
                if (!key.SequenceEqual(wanted) || value.Length != NodeRef.Size) throw new InvalidDataException("Incomplete retained PBT chunk sequence.");
                NodeRef reference = NodeRef.Read(value);
                if (reference.RlpDataOffset < 0) throw new InvalidDataException("Negative retained PBT blob offset.");
                referenced.Add(reference.BlobArenaId);
                expected++;
                continue;
            }
            if (key[0] == 0)
            {
                if (key.Length != 2 || key[1] is < 1 or > 4 || (metadataMask & (1 << key[1])) != 0) throw new InvalidDataException("Invalid retained PBT metadata key.");
                metadataMask |= 1 << key[1];
                switch (key[1])
                {
                    case 1:
                        if (!value.SequenceEqual("PBTDIFF\x01\x00"u8)) throw new InvalidDataException("Unsupported retained PBT entity format.");
                        break;
                    case 2: from = ReadState(value); break;
                    case 3: to = ReadState(value); break;
                    case 4:
                        if (value.Length != 32) throw new InvalidDataException("Invalid retained PBT tree root.");
                        root = new(value);
                        break;
                }
            }
            else if (key[0] == PbtRetainedKey.Ownership)
            {
                if (key.Length != 3 || (value.Length != 1 || value[0] != 1)) throw new InvalidDataException("Invalid retained PBT blob ownership record.");
                owners.Add(BinaryPrimitives.ReadUInt16BigEndian(key[1..]));
            }
            else
            {
                PbtRetainedKey.ValidateDescriptor(key);
                DescriptorLength(key, value, out count);
                active = count == 0 ? null : key.ToArray();
                expected = 0;
            }
        }
        if (metadataMask != 30 || expected != count || !owners.SetEquals(referenced)) throw new InvalidDataException("Incomplete retained PBT metadata or blob ownership.");
        return (new(from, to, root), owners);
    }

    private static StateId ReadState(ReadOnlySpan<byte> value)
    {
        if (value.Length != 40) throw new InvalidDataException("Invalid retained PBT StateId.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(value), new ValueHash256(value[8..]));
    }

    private void ValidateBlobChunks(scoped in ArenaByteReader reader)
    {
        using SortedTableEnumerator<ArenaByteReader, NoOpPin> scanner = new(reader, new(0, reader.Length));
        int remaining = 0;
        Span<byte> valueBuffer = stackalloc byte[255];
        while (scanner.MoveNext(reader))
        {
            ReadOnlySpan<byte> key = scanner.CurrentKey;
            if (key[0] is 0 or PbtRetainedKey.Ownership) continue;
            ReadOnlySpan<byte> value = ReadValueInto(reader, scanner.CurrentValue, valueBuffer);
            if (remaining > 0)
            {
                NodeRef reference = NodeRef.Read(value);
                int expected = Math.Min(remaining, PbtRetainedSnapshotBuilder.ChunkSize);
                ChunkBounds(reference, expected);
                remaining -= expected;
            }
            else DescriptorLength(key, value, out uint chunks);
            if (remaining == 0 && !IsChunk(key) && value[0] == 2) remaining = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(value[1..]));
        }
    }

    private long ChunkBounds(in NodeRef reference, int expectedLength)
    {
        BlobArenaFile file = _blobs.GetFile(reference.BlobArenaId);
        if (reference.RlpDataOffset < 0 || reference.RlpDataOffset >= file.Frontier) throw new InvalidDataException("Retained PBT blob reference out of bounds.");
        Span<byte> header = stackalloc byte[4];
        if (file.RandomRead(reference.RlpDataOffset, header[..1]) != 1) throw new InvalidDataException("Truncated retained PBT blob header.");
        int prefix = header[0], headerSize = 1, length;
        if (prefix < 0x80) { length = 1; headerSize = 0; }
        else if (prefix <= 0xB7)
        {
            length = prefix - 0x80;
            if (length == 1)
            {
                if (file.RandomRead((long)reference.RlpDataOffset + 1, header.Slice(1, 1)) != 1 || header[1] < 0x80)
                    throw new InvalidDataException("Noncanonical retained PBT RLP byte.");
            }
        }
        else if (prefix is >= 0xB8 and <= 0xBA)
        {
            int width = prefix - 0xB7;
            headerSize += width;
            if (file.RandomRead((long)reference.RlpDataOffset + 1, header.Slice(1, width)) != width || header[1] == 0)
                throw new InvalidDataException("Invalid retained PBT RLP length.");
            length = 0;
            for (int i = 0; i < width; i++) length = (length << 8) | header[1 + i];
            if (length <= 55) throw new InvalidDataException("Noncanonical retained PBT RLP length.");
        }
        else throw new InvalidDataException("Retained PBT blob must be a bounded RLP string.");
        if (length != expectedLength || length > PbtRetainedSnapshotBuilder.ChunkSize || (long)reference.RlpDataOffset + headerSize + length > file.Frontier)
            throw new InvalidDataException("Retained PBT blob length mismatch.");
        return (long)reference.RlpDataOffset + headerSize;
    }

    private void ReadChunk(in NodeRef reference, Span<byte> destination)
    {
        long offset = ChunkBounds(reference, destination.Length);
        if (_blobs.GetFile(reference.BlobArenaId).RandomRead(offset, destination) != destination.Length)
            throw new InvalidDataException("Truncated retained PBT payload.");
    }
}

internal sealed class PbtRetainedScanner : IDisposable
{
    private readonly PbtRetainedSnapshot _snapshot;
    private SortedTableEnumerator<ArenaByteReader, NoOpPin> _scanner;
    private bool _disposed;

    internal PbtRetainedScanner(PbtRetainedSnapshot snapshot)
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

    internal bool MoveNext()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArenaByteReader reader = _snapshot.Reservation.CreateReader();
        return _scanner.MoveNext(reader);
    }
    internal ReadOnlySpan<byte> Key => _scanner.CurrentKey;
    internal Bound Value => _scanner.CurrentValue;
    internal byte[] ReadValue()
    {
        byte[] bytes = new byte[checked((int)Value.Length)];
        ArenaByteReader reader = _snapshot.Reservation.CreateReader();
        PbtRetainedTableValidation.Read(reader, Value.Offset, bytes);
        return bytes;
    }
    internal byte[]? ReadPayload() => _snapshot.ReadPayload(Key, Value);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanner.Dispose();
        _snapshot.Dispose();
    }
}
