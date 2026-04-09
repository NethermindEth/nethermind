// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Stateless.Execution;

/// <summary>
/// Provides methods for zkVM input serialization. <see cref="Witness.Keys"/> are ignored.
/// </summary>
public static class InputSerializer
{
    private const int _minSerializedLength =
        sizeof(ulong) + // chain id
        sizeof(int) +  // block length
        sizeof(int) +  // codes length
        sizeof(int) +  // headers length
        sizeof(int);   // state length

    public static byte[] Serialize(Block block, Witness witness, ulong chainId)
    {
        ArgumentNullException.ThrowIfNull(block);

        IRlpStreamEncoder<Block> blockDecoder = Rlp.GetStreamEncoder<Block>()!; // cannot be null
        var blockLen = blockDecoder.GetLength(block, RlpBehaviors.None);
        var codesLen = GetSerializedLength(witness.Codes);
        var headersLen = GetSerializedLength(witness.Headers);
        var stateLen = GetSerializedLength(witness.State);
        var outputLen = _minSerializedLength +
            blockLen + codesLen + headersLen + stateLen;

        byte[] output = GC.AllocateUninitializedArray<byte>(outputLen);
        var offset = 0;

        WriteUInt64(chainId, output, ref offset);
        WriteInt32(blockLen, output, ref offset);

        RlpStream rlpStream = new(output) { Position = offset };
        blockDecoder.Encode(rlpStream, block, RlpBehaviors.None);
        offset += blockLen;

        if (rlpStream.Position != offset)
            throw new InvalidDataException("Unexpected block RLP length");

        WriteJaggedArray(witness.Codes, codesLen, output, ref offset);
        WriteJaggedArray(witness.Headers, headersLen, output, ref offset);
        WriteJaggedArray(witness.State, stateLen, output, ref offset);

        if (offset != outputLen)
            throw new InvalidDataException("Invalid output length");

        return output;
    }

    public static (Block, Witness, ulong) Deserialize(ReadOnlySpan<byte> input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Length, _minSerializedLength);

        var offset = 0;
        var chainId = ReadUInt64(input, ref offset);
        var blockLength = ReadInt32(input, ref offset);

        IRlpValueDecoder<Block> blockDecoder = Rlp.GetValueDecoder<Block>()!; // cannot be null
        Rlp.ValueDecoderContext blockContext = new(input.Slice(offset, blockLength));
        Block block = blockDecoder.Decode(ref blockContext, RlpBehaviors.None);
        blockContext.Check(blockLength);
        offset += blockLength;

        IOwnedReadOnlyList<byte[]> codes = ReadJaggedArray(input, ref offset);
        IOwnedReadOnlyList<byte[]> headers = ReadJaggedArray(input, ref offset);
        IOwnedReadOnlyList<byte[]> state = ReadJaggedArray(input, ref offset);

        if (offset != input.Length)
            throw new InvalidDataException("Invalid input or section length");

        Witness witness = new()
        {
            Codes = codes,
            Headers = headers,
            Keys = ArrayPoolList<byte[]>.Empty(),
            State = state
        };

        return (block, witness, chainId);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, sizeof(int)));
        offset += sizeof(int);

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt32(int value, Span<byte> destination, ref int offset)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, sizeof(int)), value);
        offset += sizeof(int);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong ReadUInt64(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));
        offset += sizeof(ulong);

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt64(ulong value, Span<byte> destination, ref int offset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, sizeof(ulong)), value);
        offset += sizeof(ulong);
    }

    private static ArrayPoolList<byte[]> ReadJaggedArray(ReadOnlySpan<byte> source, ref int offset)
    {
        var sectionLen = ReadInt32(source, ref offset);

        if (sectionLen == 0)
            return ArrayPoolList<byte[]>.Empty();

        var count = ReadInt32(source, ref offset);
        ArrayPoolList<byte[]> output = new(count, count);

        for (int i = 0; i < count; i++)
        {
            var len = ReadInt32(source, ref offset);

            output[i] = source.Slice(offset, len).ToArray();
            offset += len;
        }

        return output;
    }

    private static void WriteJaggedArray(
        IOwnedReadOnlyList<byte[]> value,
        int sectionLength,
        Span<byte> destination,
        ref int offset)
    {
        WriteInt32(sectionLength, destination, ref offset);

        if (value.Count == 0)
            return;

        WriteInt32(value.Count, destination, ref offset);

        for (var i = 0; i < value.Count; i++)
        {
            var len = value[i].Length;

            WriteInt32(len, destination, ref offset);

            value[i].CopyTo(destination.Slice(offset, len));
            offset += len;
        }
    }

    private static int GetSerializedLength(IOwnedReadOnlyList<byte[]> value)
    {
        if (value.Count == 0)
            return 0;

        var len = sizeof(int);

        for (int i = 0; i < value.Count; i++)
            len += sizeof(int) + value[i].Length;

        return len;
    }
}
