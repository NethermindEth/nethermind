// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.PersistedSnapshots;

/// <summary>The value side of a retained snapshot table: its metadata records, and the descriptor each entity key maps to.</summary>
/// <remarks>
/// A descriptor is <see cref="NullMarker"/> for a tombstone, <see cref="InlineMarker"/> followed by a payload of at most
/// <see cref="InlineLimit"/> bytes, or <see cref="ChunkedMarker"/> followed by the payload length and chunk count, each u32 LE,
/// for a payload stored in <see cref="ChunkSize"/> blob chunks under <see cref="PbtRetainedKey.Chunk"/> keys.
/// </remarks>
internal static class PbtRetainedFormat
{
    internal const int ChunkSize = 65536;
    internal const int InlineLimit = 254;
    internal const byte NullMarker = 0;
    internal const byte InlineMarker = 1;
    internal const byte ChunkedMarker = 2;
    internal const int ChunkedDescriptorLength = 1 + 2 * sizeof(uint);

    private const byte FormatRecord = 1;
    private const byte FromRecord = 2;
    private const byte ToRecord = 3;
    private const byte TreeRootRecord = 4;
    private const int AllRecords = 1 << FormatRecord | 1 << FromRecord | 1 << ToRecord | 1 << TreeRootRecord;
    private const int StateLength = sizeof(ulong) + ValueHash256.MemorySize;

    private static ReadOnlySpan<byte> Magic => "PBTDIFF\x01\x00"u8;

    internal static void WriteMetadata<TWriter>(ref SortedTableBuilder<TWriter> table, in PbtRetainedMetadata metadata)
        where TWriter : IByteBufferWriter
    {
        table.Add([PbtRetainedKey.Metadata, FormatRecord], Magic);
        Span<byte> state = stackalloc byte[StateLength];
        WriteState(state, metadata.From);
        table.Add([PbtRetainedKey.Metadata, FromRecord], state);
        WriteState(state, metadata.To);
        table.Add([PbtRetainedKey.Metadata, ToRecord], state);
        table.Add([PbtRetainedKey.Metadata, TreeRootRecord], metadata.TreeRoot.Bytes);
    }

    internal static void WriteChunkedDescriptor(Span<byte> descriptor, int length, uint chunkCount)
    {
        descriptor[0] = ChunkedMarker;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[1..], checked((uint)length));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[5..], chunkCount);
    }

    internal static uint ChunkCount(long length) => checked((uint)((length + ChunkSize - 1) / ChunkSize));

    /// <summary>Validates <paramref name="value"/> as the descriptor of entity <paramref name="key"/>.</summary>
    /// <returns>The payload length, or -1 for a tombstone; <paramref name="chunks"/> is zero unless the payload is chunked.</returns>
    internal static int PayloadLength(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, out uint chunks)
    {
        chunks = 0;
        if (value.IsEmpty) throw new InvalidDataException("Empty retained PBT descriptor.");
        bool nullable = key[0] is PbtRetainedKey.AccountGroup or PbtRetainedKey.CodeGroup or PbtRetainedKey.StorageGroup
            || key[0] == PbtRetainedKey.Address && key[33] == PbtRetainedKey.Account;
        switch (value[0])
        {
            case NullMarker when nullable && value.Length == 1: return -1;
            case InlineMarker:
                int inline = value.Length - 1;
                ValidateLength(key, inline);
                if (key[0] == PbtRetainedKey.Address && key[33] == PbtRetainedKey.Clear && value[1] > 1)
                    throw new InvalidDataException("Invalid retained PBT clear value.");
                return inline;
            case ChunkedMarker when value.Length == ChunkedDescriptorLength:
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(value[1..]);
                chunks = BinaryPrimitives.ReadUInt32LittleEndian(value[5..]);
                if (length <= InlineLimit || length > int.MaxValue || chunks != ChunkCount(length))
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
                PbtRetainedKey.Account => PbtAccount.IsValidEncodedLength(length),
                // An empty run is retained as an empty payload, though persistence never encodes one.
                _ => length == 0 || SlotRun.IsValidEncodedLength(length),
            },
            PbtRetainedKey.Code => true,
            _ => length is > 0 and <= PbtNodeGroupCodec.MaxPayloadLength,
        };
        if (!valid) throw new InvalidDataException("Invalid retained PBT entity payload length.");
    }

    private static void WriteState(Span<byte> bytes, in StateId state)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, state.BlockNumber);
        state.StateRoot.Bytes.CopyTo(bytes[8..]);
    }

    private static StateId ReadState(ReadOnlySpan<byte> value)
    {
        if (value.Length != StateLength) throw new InvalidDataException("Invalid retained PBT StateId.");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(value), new ValueHash256(value[8..]));
    }

    /// <summary>Collects the metadata records of a table, rejecting unknown, repeated or malformed ones.</summary>
    internal struct MetadataReader
    {
        private int _seen;
        private StateId _from;
        private StateId _to;
        private ValueHash256 _treeRoot;

        public readonly bool IsComplete => _seen == AllRecords;
        public readonly PbtRetainedMetadata Metadata => new(_from, _to, _treeRoot);

        public void Read(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
        {
            if (key.Length != 2 || key[1] is < FormatRecord or > TreeRootRecord || (_seen & (1 << key[1])) != 0)
                throw new InvalidDataException("Invalid retained PBT metadata key.");
            _seen |= 1 << key[1];
            switch (key[1])
            {
                case FormatRecord:
                    if (!value.SequenceEqual(Magic)) throw new InvalidDataException("Unsupported retained PBT entity format.");
                    break;
                case FromRecord: _from = ReadState(value); break;
                case ToRecord: _to = ReadState(value); break;
                case TreeRootRecord:
                    if (value.Length != ValueHash256.MemorySize) throw new InvalidDataException("Invalid retained PBT tree root.");
                    _treeRoot = new(value);
                    break;
            }
        }
    }
}
