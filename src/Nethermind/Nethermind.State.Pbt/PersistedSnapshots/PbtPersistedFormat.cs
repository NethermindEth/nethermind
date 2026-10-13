// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core.Crypto;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Pbt.Common;

namespace Nethermind.State.Pbt.PersistedSnapshots;

/// <summary>The value side of a persisted snapshot table: its metadata records, and the descriptor each entity key maps to.</summary>
/// <remarks>
/// A descriptor is <see cref="NullMarker"/> for a tombstone, <see cref="InlineMarker"/> followed by a payload of at most
/// <see cref="InlineLimit"/> bytes, or <see cref="ChunkedMarker"/> followed by the payload length and chunk count, each u32 LE,
/// for a payload stored in <see cref="ChunkSize"/> blob chunks under <see cref="PbtPersistedKey.Chunk"/> keys.
/// </remarks>
public static class PbtPersistedFormat
{
    public const int ChunkSize = 65536;
    public const int InlineLimit = 254;
    public const byte NullMarker = 0;
    public const byte InlineMarker = 1;
    private const byte ChunkedMarker = 2;
    public const int ChunkedDescriptorLength = 1 + 2 * sizeof(uint);

    private const byte FormatRecord = 1;
    private const byte FromRecord = 2;
    private const byte ToRecord = 3;
    private const byte TreeRootRecord = 4;
    private const int StateLength = sizeof(ulong) + ValueHash256.MemorySize;

    private static ReadOnlySpan<byte> Magic => "PBTDIFF\x02\x00"u8;

    public static void WriteMetadata<TWriter>(ref SortedTableBuilder<TWriter> table, in PbtPersistedMetadata metadata)
        where TWriter : IByteBufferWriter
    {
        table.Add([PbtPersistedKey.Metadata, FormatRecord], Magic);
        Span<byte> state = stackalloc byte[StateLength];
        WriteState(state, metadata.From);
        table.Add([PbtPersistedKey.Metadata, FromRecord], state);
        WriteState(state, metadata.To);
        table.Add([PbtPersistedKey.Metadata, ToRecord], state);
        table.Add([PbtPersistedKey.Metadata, TreeRootRecord], metadata.TreeRoot.Bytes);
    }

    public static void WriteChunkedDescriptor(Span<byte> descriptor, int length, uint chunkCount)
    {
        descriptor[0] = ChunkedMarker;
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[1..], checked((uint)length));
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[5..], chunkCount);
    }

    public static uint ChunkCount(long length) => checked((uint)((length + ChunkSize - 1) / ChunkSize));

    /// <summary>Validates <paramref name="value"/> as the descriptor of entity <paramref name="key"/>.</summary>
    /// <returns>The payload length, or -1 for a tombstone; <paramref name="chunks"/> is zero unless the payload is chunked.</returns>
    public static int PayloadLength(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, out uint chunks)
    {
        chunks = 0;
        if (value.IsEmpty) throw new InvalidDataException("Empty persisted PBT descriptor.");
        bool nullable = key[0] is PbtPersistedKey.AccountGroup or PbtPersistedKey.CodeGroup or PbtPersistedKey.StorageGroup
            || key[0] == PbtPersistedKey.Address && key[33] == PbtPersistedKey.Account;
        switch (value[0])
        {
            case NullMarker when nullable && value.Length == 1: return -1;
            case InlineMarker:
                int inline = value.Length - 1;
                ValidateLength(key, inline);
                if (key[0] == PbtPersistedKey.Address && key[33] == PbtPersistedKey.Clear && value[1] > 1)
                    throw new InvalidDataException("Invalid persisted PBT clear value.");
                return inline;
            case ChunkedMarker when value.Length == ChunkedDescriptorLength:
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(value[1..]);
                chunks = BinaryPrimitives.ReadUInt32LittleEndian(value[5..]);
                if (length <= InlineLimit || length > int.MaxValue || chunks != ChunkCount(length))
                    throw new InvalidDataException("Invalid persisted PBT chunk descriptor.");
                ValidateLength(key, (int)length);
                return (int)length;
            default: throw new InvalidDataException("Invalid persisted PBT descriptor marker.");
        }
    }

    private static void ValidateLength(ReadOnlySpan<byte> key, int length)
    {
        bool valid = key[0] switch
        {
            PbtPersistedKey.Address => key[33] switch
            {
                PbtPersistedKey.Clear => length == 1,
                PbtPersistedKey.Account => PbtAccount.IsValidEncodedLength(length),
                // An empty run is persisted as an empty payload, though persistence never encodes one.
                _ => length == 0 || SlotRun.IsValidEncodedLength(length),
            },
            PbtPersistedKey.Code => true,
            _ => length is > 0 and <= PbtNodeGroupCodec.MaxPayloadLength,
        };
        if (!valid) throw new InvalidDataException("Invalid persisted PBT entity payload length.");
    }

    private static void WriteState(Span<byte> bytes, in StateId state)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, state.BlockNumber);
        state.StateRoot.Bytes.CopyTo(bytes[8..]);
    }

    public static ValueHash256 ReadTreeRoot(scoped in ArenaByteReader reader)
    {
        Span<byte> treeRoot = stackalloc byte[ValueHash256.MemorySize];
        if (!SortedTableReader.TrySeek<ArenaByteReader, NoOpPin>(reader, new(0, reader.Length), [PbtPersistedKey.Metadata, TreeRootRecord], out Bound bound)
            || bound.Length != treeRoot.Length || !reader.TryRead(bound.Offset, treeRoot))
            throw new InvalidDataException("Missing persisted PBT tree root.");
        return new(treeRoot);
    }
}
