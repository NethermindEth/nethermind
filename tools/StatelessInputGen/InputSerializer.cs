// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Serialization.Rlp;

namespace Nethermind.StatelessInputGen;

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
        => Serialize(block, witness, chainId, null);

    public static byte[] Serialize(Block block, Witness witness, ulong chainId, byte[]? chainConfigJson)
    {
        ArgumentNullException.ThrowIfNull(block);

        IRlpDecoder<Block> blockDecoder = Rlp.GetDecoder<Block>()!; // cannot be null
        int blockLen = blockDecoder.GetLength(block, RlpBehaviors.None);
        int codesLen = GetSerializedLength(witness.Codes);
        int headersLen = GetSerializedLength(witness.Headers);
        int stateLen = GetSerializedLength(witness.State);
        int chainConfigLen = chainConfigJson?.Length ?? 0;
        int chainConfigSectionLen = chainConfigLen == 0 ? 0 : sizeof(int) + chainConfigLen;
        int outputLen = _minSerializedLength +
            blockLen + codesLen + headersLen + stateLen + chainConfigSectionLen;

        byte[] output = GC.AllocateUninitializedArray<byte>(outputLen);
        int offset = 0;

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

        if (chainConfigLen != 0)
        {
            WriteInt32(chainConfigLen, output, ref offset);
            chainConfigJson!.AsSpan().CopyTo(output.AsSpan(offset, chainConfigLen));
            offset += chainConfigLen;
        }

        if (offset != outputLen)
            throw new InvalidDataException("Invalid output length");

        return output;
    }

    public static (Block, Witness, ulong) Deserialize(ReadOnlySpan<byte> input)
    {
        (Block block, Witness witness, ulong chainId, _) = DeserializeWithChainConfig(input);
        return (block, witness, chainId);
    }

    public static (Block, Witness, ulong, byte[]?) DeserializeWithChainConfig(ReadOnlySpan<byte> input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Length, _minSerializedLength);

        int offset = 0;
        ulong chainId = ReadUInt64(input, ref offset);
        int blockLength = ReadInt32(input, ref offset);

        IRlpDecoder<Block> blockDecoder = Rlp.GetDecoder<Block>()!; // cannot be null
        Rlp.ValueDecoderContext blockContext = new(input.Slice(offset, blockLength));
        Block block = blockDecoder.Decode(ref blockContext, RlpBehaviors.None);
        blockContext.Check(blockLength);
        offset += blockLength;

        IOwnedReadOnlyList<byte[]> codes = ReadJaggedArray(input, ref offset);
        IOwnedReadOnlyList<byte[]> headers = ReadJaggedArray(input, ref offset);
        IOwnedReadOnlyList<byte[]> state = ReadJaggedArray(input, ref offset);

        // Optional trailing section: chain_config_json (i32 LE length + UTF-8 JSON
        // bytes). Allows the executor to build a SpecProvider from an arbitrary
        // chain.
        byte[]? chainConfigJson = null;
        if (offset < input.Length)
        {
            int chainConfigLen = ReadInt32(input, ref offset);
            if (chainConfigLen < 0 || offset + chainConfigLen > input.Length)
                throw new InvalidDataException("Invalid chain_config_json length");
            chainConfigJson = input.Slice(offset, chainConfigLen).ToArray();
            offset += chainConfigLen;
        }

        if (offset != input.Length)
            throw new InvalidDataException("Invalid input or section length");

        Witness witness = new()
        {
            Codes = codes,
            Headers = headers,
            Keys = ArrayPoolList<byte[]>.Empty(),
            State = state
        };

        return (block, witness, chainId, chainConfigJson);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        int value = BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, sizeof(int)));
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
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(source.Slice(offset, sizeof(ulong)));
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
        int sectionLen = ReadInt32(source, ref offset);

        if (sectionLen == 0)
            return ArrayPoolList<byte[]>.Empty();

        int count = ReadInt32(source, ref offset);
        ArrayPoolList<byte[]> output = new(count, count);

        for (int i = 0; i < count; i++)
        {
            int len = ReadInt32(source, ref offset);

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

        ReadOnlySpan<byte[]> valueSpan = value.AsSpan();
        WriteInt32(valueSpan.Length, destination, ref offset);

        for (int i = 0; i < valueSpan.Length; i++)
        {
            byte[] item = valueSpan[i];
            int len = item.Length;

            WriteInt32(len, destination, ref offset);

            item.CopyTo(destination.Slice(offset, len));
            offset += len;
        }
    }

    private static int GetSerializedLength(IOwnedReadOnlyList<byte[]> value)
    {
        if (value.Count == 0)
            return 0;

        int len = sizeof(int);
        ReadOnlySpan<byte[]> valueSpan = value.AsSpan();

        for (int i = 0; i < valueSpan.Length; i++)
            len += sizeof(int) + valueSpan[i].Length;

        return len;
    }
}
