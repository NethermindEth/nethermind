// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Image;

/// <summary>Streams the canonical EIP-8347 snapshot artifact without trusting its claimed root.</summary>
/// <remarks>Streams remain caller-owned. Input must be consumed to completion to validate the trailer and EOF, and
/// must be seekable, since the claimed root trails the records and each storage record's account is checked against a
/// second pass over the header records. The artifact holds tagged records: typed account headers and stem-grouped code
/// and storage leaves; the codec translates them to and from the ascending PBT leaf stream they derive. Writers require
/// strictly ordered leaves; they do not sort or retain the input. Cancellation is checked per record.</remarks>
internal static class PbtSnapshotCodec
{
    private const byte NoCodeHeader = 0x00;
    private const byte ContractHeader = 0x01;
    private const byte DelegationHeader = 0x02;
    private const byte CodeGroup = 0x03;
    private const byte FirstStorageGroup = 0x04;
    private const byte NextStorageGroup = 0x05;
    private const byte FirstSingleStorageGroup = 0x06;
    private const byte NextSingleStorageGroup = 0x07;
    private const byte End = 0x08;
    private const int HeaderStorageSlots = 64;
    private const int DelegationLength = 23;
    private const int GroupWidth = 256;

    /// <summary>Reads the claimed PBT root from the trailer, leaving the stream's position unchanged.</summary>
    public static ValueHash256 ReadRoot(Stream source)
    {
        if (source.Length < ValueHash256.MemorySize) throw new InvalidDataException("Truncated snapshot trailer.");
        long position = source.Position;
        source.Position = source.Length - ValueHash256.MemorySize;
        ValueHash256 root = ReadHash(source);
        source.Position = position;
        return root;
    }

    public static IEnumerable<RebuildEntry> ReadLeaves(Stream source, CancellationToken cancellationToken = default)
    {
        List<RebuildEntry> leaves = new(GroupWidth);
        using HeaderCursor headers = new(source, source.Position);
        byte[] codePrefix = [Eip8297KeyDerivation.CodeZone];
        byte[] storagePrefix = new byte[1 + ValueHash256.MemorySize];
        storagePrefix[0] = Eip8297KeyDerivation.StorageZone;
        ValueHash256? previousHeader = null, previousCode = null, previousStorage = null, previousStem = null;
        int tag;
        while ((tag = ReadByte(source)) != End)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (tag)
            {
                case <= DelegationHeader when previousCode is null && previousStorage is null:
                    previousHeader = ReadHeaderRecord(source, tag, previousHeader, leaves);
                    break;
                case CodeGroup when previousStorage is null:
                    previousCode = ReadGroup(source, codePrefix, previousCode, single: false, leaves);
                    break;
                case FirstStorageGroup or FirstSingleStorageGroup:
                    ValueHash256 addressHash = ReadAscendingHash(source, previousStorage);
                    headers.Require(addressHash);
                    previousStorage = addressHash;
                    addressHash.Bytes.CopyTo(storagePrefix.AsSpan(1));
                    previousStem = ReadGroup(source, storagePrefix, null, tag == FirstSingleStorageGroup, leaves);
                    break;
                case (NextStorageGroup or NextSingleStorageGroup) when previousStorage is not null:
                    previousStem = ReadGroup(source, storagePrefix, previousStem, tag == NextSingleStorageGroup, leaves);
                    break;
                default:
                    throw new InvalidDataException("Unexpected snapshot record tag.");
            }
            if (tag is FirstStorageGroup or NextStorageGroup && leaves.Count == 1)
                throw new InvalidDataException("A one-slot storage group must use a single-slot tag.");
            foreach (RebuildEntry leaf in leaves) yield return leaf;
            leaves.Clear();
        }
        cancellationToken.ThrowIfCancellationRequested();
        ReadHash(source);
        if (source.ReadByte() != -1) throw new InvalidDataException("Trailing snapshot bytes.");
    }

    /// <summary>Writes the snapshot of <paramref name="leaves"/> in one pass, claiming the root that
    /// <paramref name="calculateRoot"/> folds from the same leaves as they are written.</summary>
    /// <param name="calculateRoot">Must consume every leaf of the stream it is given.</param>
    public static void Write(Stream destination, IEnumerable<RebuildEntry> leaves,
        Func<IEnumerable<RebuildEntry>, ValueHash256> calculateRoot, CancellationToken cancellationToken = default)
    {
        StemWriter writer = new(destination);
        bool written = false;
        ValueHash256 root = calculateRoot(Written());
        if (!written) throw new InvalidOperationException("The root calculation did not consume every snapshot leaf.");
        cancellationToken.ThrowIfCancellationRequested();
        writer.Finish(root);

        IEnumerable<RebuildEntry> Written()
        {
            PbtStorageTreeKey previous = default;
            foreach (RebuildEntry entry in leaves)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Validate(entry, previous);
                previous = entry.Key;
                writer.Add(entry);
                yield return entry;
            }
            written = true;
        }
    }

    /// <summary>Fills <paramref name="buffer"/> from the artifact, reporting a stream that ends mid-record as malformed input.</summary>
    internal static void ReadRecord(Stream source, Span<byte> buffer)
    {
        if (source.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) != buffer.Length)
            throw new InvalidDataException("Truncated artifact record.");
    }

    private static ValueHash256 ReadHeaderRecord(Stream source, int tag, ValueHash256? previous, List<RebuildEntry> leaves)
    {
        ValueHash256 addressHash = ReadAscendingHash(source, previous);
        Span<byte> nonce = stackalloc byte[sizeof(ulong)];
        Span<byte> balance = stackalloc byte[16];
        Span<byte> codeSizeBytes = stackalloc byte[sizeof(uint)];
        Span<byte> delegation = stackalloc byte[32];
        bool noNonce = ReadInteger(source, nonce) == 0;
        bool noBalance = ReadInteger(source, balance) == 0;
        uint codeSize = 0;
        byte codeLeafIndex = PbtKeyDerivation.CodeHashLeafKey;
        ValueHash256 codeLeaf;
        switch (tag)
        {
            case NoCodeHeader:
                if (noNonce && noBalance) throw new InvalidDataException("Empty account violates EIP-7523.");
                codeLeaf = Keccak.OfAnEmptyString.ValueHash256;
                break;
            case ContractHeader:
                codeLeaf = ReadHash(source);
                if (ReadInteger(source, codeSizeBytes) == 0) throw new InvalidDataException("Contract account has a zero code size.");
                codeSize = BinaryPrimitives.ReadUInt32BigEndian(codeSizeBytes);
                break;
            case DelegationHeader:
                delegation.Clear();
                Eip7702Constants.DelegationHeader.CopyTo(delegation);
                ReadRecord(source, delegation[Eip7702Constants.DelegationHeaderLength..DelegationLength]);
                codeLeaf = new ValueHash256(delegation);
                codeLeafIndex = PbtKeyDerivation.DelegationLeafKey;
                codeSize = DelegationLength;
                break;
            default:
                throw new InvalidDataException("Unknown snapshot account kind.");
        }

        Span<byte> basicData = stackalloc byte[32];
        PbtKeyDerivation.PackBasicData(basicData, codeSize, new UInt256(nonce, isBigEndian: true), new UInt256(balance, isBigEndian: true));
        leaves.Add(AccountLeaf(addressHash, PbtKeyDerivation.BasicDataLeafKey, new ValueHash256(basicData)));
        leaves.Add(AccountLeaf(addressHash, codeLeafIndex, codeLeaf));
        int slotCount = ReadByte(source);
        int previousSlot = -1;
        for (int index = 0; index < slotCount; index++)
        {
            int slot = ReadByte(source);
            if (slot <= previousSlot || slot >= HeaderStorageSlots)
                throw new InvalidDataException("Header slots must be strictly ascending and below HEADER_STORAGE_SLOTS.");
            previousSlot = slot;
            leaves.Add(AccountLeaf(addressHash, (byte)(PbtKeyDerivation.HeaderStorageOffset + slot), ReadValue(source)));
        }
        return addressHash;
    }

    private static RebuildEntry AccountLeaf(in ValueHash256 addressHash, byte subIndex, in ValueHash256 value) =>
        new((PbtStorageTreeKey)PbtStateKey.Account(addressHash, subIndex), value);

    /// <summary>Reads one stem group, whose leaf keys are <paramref name="prefix"/>, the stem hash and each entry's sub-index.</summary>
    /// <param name="single">Whether the group holds one entry and so omits its entry count.</param>
    private static ValueHash256 ReadGroup(Stream source, byte[] prefix, ValueHash256? previous, bool single, List<RebuildEntry> leaves)
    {
        ValueHash256 stemHash = ReadAscendingHash(source, previous);
        Span<byte> key = stackalloc byte[prefix.Length + ValueHash256.MemorySize + 1];
        prefix.CopyTo(key);
        stemHash.Bytes.CopyTo(key[prefix.Length..]);
        int count = single ? 1 : ReadByte(source) + 1;
        int previousIndex = -1;
        for (int index = 0; index < count; index++)
        {
            int subIndex = ReadByte(source);
            if (subIndex <= previousIndex) throw new InvalidDataException("Group entries must be strictly ascending.");
            previousIndex = subIndex;
            key[^1] = (byte)subIndex;
            leaves.Add(new(new PbtStorageTreeKey(key), ReadValue(source)));
        }
        return stemHash;
    }

    private static ValueHash256 ReadAscendingHash(Stream source, ValueHash256? previous)
    {
        ValueHash256 hash = ReadHash(source);
        if (previous is { } previousHash && previousHash.Bytes.SequenceCompareTo(hash.Bytes) >= 0)
            throw new InvalidDataException("Snapshot records must be strictly ordered.");
        return hash;
    }

    private static ValueHash256 ReadHash(Stream source)
    {
        Span<byte> hash = stackalloc byte[ValueHash256.MemorySize];
        ReadRecord(source, hash);
        return new ValueHash256(hash);
    }

    private static ValueHash256 ReadValue(Stream source)
    {
        Span<byte> value = stackalloc byte[ValueHash256.MemorySize];
        if (ReadInteger(source, value) == 0) throw new InvalidDataException("Snapshot leaf values must be nonzero.");
        return new ValueHash256(value);
    }

    /// <summary>Reads a length-prefixed big-endian integer right-aligned into <paramref name="destination"/>, whose size is its width.</summary>
    /// <returns>The encoded length, zero for the integer zero.</returns>
    private static int ReadInteger(Stream source, Span<byte> destination)
    {
        int length = ReadByte(source);
        if (length > destination.Length) throw new InvalidDataException("Snapshot integer exceeds its width.");
        destination.Clear();
        Span<byte> value = destination[(destination.Length - length)..];
        ReadRecord(source, value);
        if (length != 0 && value[0] == 0) throw new InvalidDataException("Noncanonical snapshot integer.");
        return length;
    }

    private static int ReadByte(Stream source) =>
        source.ReadByte() is var value and >= 0 ? value : throw new InvalidDataException("Truncated artifact record.");

    private static void Validate(RebuildEntry entry, in PbtStorageTreeKey previous)
    {
        ReadOnlySpan<byte> key = entry.Key.Bytes;
        if (key.IsEmpty || key.Length != (key[0] switch { Eip8297KeyDerivation.AccountZone or Eip8297KeyDerivation.CodeZone => 34, Eip8297KeyDerivation.StorageZone => 66, _ => -1 }))
            throw new InvalidDataException("Invalid snapshot key zone or length.");
        if (entry.Leaf == default || (previous.Length != 0 && previous.CompareTo(entry.Key) >= 0))
            throw new InvalidDataException("Snapshot leaves must be nonzero and strictly ordered.");
    }

    /// <summary>Walks the header records alongside the storage records, rejecting storage whose account has no header record.</summary>
    /// <remarks>Both ascend by address hash, so one forward pass over a second view of the headers suffices.
    /// Buffering that view limits the source seeks it costs to one per buffer refill.</remarks>
    private sealed class HeaderCursor(Stream source, long headersStart) : IDisposable
    {
        private readonly BufferedStream _headers = new(new PositionedReader(source, headersStart), 1 << 16);
        private readonly List<RebuildEntry> _discarded = [];
        private bool _exhausted;
        private ValueHash256? _current;

        public void Require(in ValueHash256 addressHash)
        {
            while (!_exhausted && (_current is not { } current || current.Bytes.SequenceCompareTo(addressHash.Bytes) < 0))
            {
                int tag = ReadByte(_headers);
                _exhausted = tag > DelegationHeader;
                if (_exhausted) break;
                _current = ReadHeaderRecord(_headers, tag, _current, _discarded);
                _discarded.Clear();
            }
            if (_current != addressHash) throw new InvalidDataException("Snapshot holds storage of an account it has no header for.");
        }

        public void Dispose() => _headers.Dispose();
    }

    /// <summary>Reads <paramref name="source"/> from a position of its own, restoring the source's position after every read.</summary>
    private sealed class PositionedReader(Stream source, long position) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long resume = source.Position;
            source.Position = position;
            int read = source.Read(buffer);
            position += read;
            source.Position = resume;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Buffers one stem of the ascending leaf stream and writes it as a tagged record once the stem ends.</summary>
    private sealed class StemWriter(Stream destination)
    {
        private readonly byte[] _stem = new byte[PbtStorageTreeKey.MaxLength - 1];
        private readonly byte[] _subIndexes = new byte[GroupWidth];
        private readonly ValueHash256[] _values = new ValueHash256[GroupWidth];
        private readonly byte[] _storageAddress = new byte[ValueHash256.MemorySize];
        // Large enough for the widest write: a first storage group's tag and address, then a full group.
        private readonly byte[] _record = new byte[1 + 2 * ValueHash256.MemorySize + 1 + GroupWidth * (2 + ValueHash256.MemorySize)];
        private int _stemLength;
        private int _count;
        private int _position;
        private bool _inStorage;

        public void Add(in RebuildEntry entry)
        {
            ReadOnlySpan<byte> stem = entry.Key.Bytes[..^1];
            if (_count == 0 || !stem.SequenceEqual(_stem.AsSpan(0, _stemLength)))
            {
                Flush();
                stem.CopyTo(_stem);
                _stemLength = stem.Length;
            }
            _subIndexes[_count] = entry.Key.Bytes[^1];
            _values[_count++] = entry.Leaf;
        }

        public void Finish(in ValueHash256 root)
        {
            Flush();
            destination.WriteByte(End);
            destination.Write(root.Bytes);
        }

        private void Flush()
        {
            if (_count == 0) return;
            ReadOnlySpan<byte> stem = _stem.AsSpan(0, _stemLength);
            switch (stem[0])
            {
                case Eip8297KeyDerivation.AccountZone:
                    WriteHeader(stem[1..]);
                    break;
                case Eip8297KeyDerivation.CodeZone:
                    _record[_position++] = CodeGroup;
                    AppendGroup(stem[1..], single: false);
                    break;
                default:
                    AppendStorageGroup(stem[1..(1 + ValueHash256.MemorySize)], stem[(1 + ValueHash256.MemorySize)..]);
                    break;
            }
            destination.Write(_record.AsSpan(0, _position));
            _position = 0;
            _count = 0;
        }

        private void WriteHeader(ReadOnlySpan<byte> addressHash)
        {
            ValueHash256? basicData = null, codeHash = null, delegation = null;
            int firstSlot = _count;
            for (int index = _count - 1; index >= 0; index--)
            {
                switch (_subIndexes[index])
                {
                    case PbtKeyDerivation.BasicDataLeafKey: basicData = _values[index]; break;
                    case PbtKeyDerivation.CodeHashLeafKey: codeHash = _values[index]; break;
                    case PbtKeyDerivation.DelegationLeafKey: delegation = _values[index]; break;
                    case >= PbtKeyDerivation.HeaderStorageOffset and < PbtKeyDerivation.HeaderStorageOffset + HeaderStorageSlots: firstSlot = index; break;
                    default: throw new InvalidDataException("Snapshot holds a leaf at a reserved account sub-index.");
                }
            }
            if (basicData is not { } basic || basic.Bytes[..4].IndexOfAnyExcept((byte)0) >= 0)
                throw new InvalidDataException("Account header needs a basic-data leaf with zero version and reserved bytes.");
            uint codeSize = PbtKeyDerivation.ReadBasicDataCodeSize(basic.Bytes);
            ReadOnlySpan<byte> nonce = basic.Bytes[8..16];
            ReadOnlySpan<byte> balance = basic.Bytes[16..];

            // The tag names the account kind, which is only known once the leaves below are checked.
            int tagPosition = _position++;
            Append(addressHash);
            AppendInteger(nonce);
            AppendInteger(balance);
            if (delegation is { } delegated)
            {
                if (codeHash is not null || codeSize != DelegationLength || !Eip7702Constants.IsDelegatedCode(delegated.Bytes[..DelegationLength]) ||
                    delegated.Bytes[DelegationLength..].IndexOfAnyExcept((byte)0) >= 0)
                    throw new InvalidDataException("Invalid delegation header.");
                _record[tagPosition] = DelegationHeader;
                Append(delegated.Bytes[Eip7702Constants.DelegationHeaderLength..DelegationLength]);
            }
            else if (codeHash is not { } hash) throw new InvalidDataException("Account has no code-hash leaf.");
            else if (codeSize == 0)
            {
                if (hash != Keccak.OfAnEmptyString.ValueHash256) throw new InvalidDataException("Codeless account has a nonempty code hash.");
                if (nonce.IndexOfAnyExcept((byte)0) < 0 && balance.IndexOfAnyExcept((byte)0) < 0) throw new InvalidDataException("Empty account violates EIP-7523.");
                _record[tagPosition] = NoCodeHeader;
            }
            else
            {
                _record[tagPosition] = ContractHeader;
                Append(hash.Bytes);
                AppendInteger(basic.Bytes.Slice(4, sizeof(uint)));
            }

            _record[_position++] = (byte)(_count - firstSlot);
            for (int index = firstSlot; index < _count; index++)
            {
                _record[_position++] = (byte)(_subIndexes[index] - PbtKeyDerivation.HeaderStorageOffset);
                AppendInteger(_values[index].Bytes);
            }
        }

        private void AppendStorageGroup(ReadOnlySpan<byte> addressHash, ReadOnlySpan<byte> stemHash)
        {
            bool single = _count == 1;
            if (_inStorage && addressHash.SequenceEqual(_storageAddress)) _record[_position++] = single ? NextSingleStorageGroup : NextStorageGroup;
            else
            {
                _inStorage = true;
                addressHash.CopyTo(_storageAddress);
                _record[_position++] = single ? FirstSingleStorageGroup : FirstStorageGroup;
                Append(addressHash);
            }
            AppendGroup(stemHash, single);
        }

        private void AppendGroup(ReadOnlySpan<byte> stemHash, bool single)
        {
            Append(stemHash);
            if (!single) _record[_position++] = (byte)(_count - 1);
            for (int index = 0; index < _count; index++)
            {
                _record[_position++] = _subIndexes[index];
                AppendInteger(_values[index].Bytes);
            }
        }

        private void AppendInteger(ReadOnlySpan<byte> bigEndian)
        {
            int leadingZeros = bigEndian.IndexOfAnyExcept((byte)0);
            ReadOnlySpan<byte> value = leadingZeros < 0 ? [] : bigEndian[leadingZeros..];
            _record[_position++] = (byte)value.Length;
            Append(value);
        }

        private void Append(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(_record.AsSpan(_position));
            _position += bytes.Length;
        }
    }
}
