// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Stateless.Execution;

public static class InputSerializer
{
    private static readonly BlockDecoder _blockDecoder = new();

    public static byte[] Serialize(Block block, Witness witness, uint chainId)
    {
        ArgumentNullException.ThrowIfNull(block);

        var blockLen = _blockDecoder.GetLength(block, RlpBehaviors.None);
        var codesLen = GetSerializedLength(witness.Codes);
        var headersLen = GetSerializedLength(witness.Headers);
        var keysLen = GetSerializedLength(witness.Keys);
        var stateLen = GetSerializedLength(witness.State);
        var outputLen = sizeof(uint) + // chain id
            sizeof(int) + blockLen +   // length | block
            sizeof(int) + codesLen +   // length | codes 
            sizeof(int) + headersLen + // length | headers
            sizeof(int) + keysLen +    // length | keys
            sizeof(int) + stateLen;    // length | state

        byte[] output = GC.AllocateUninitializedArray<byte>(outputLen);
        var offset = 0;

        WriteUInt32(chainId, output, ref offset);
        WriteInt32(blockLen, output, ref offset);

        RlpStream rlpStream = new(output) { Position = offset };
        _blockDecoder.Encode(rlpStream, block, RlpBehaviors.None);
        offset += blockLen;

        Debug.Assert(rlpStream.Position == offset, "Unexpected block RLP length");

        WriteJaggedArray(witness.Codes, codesLen, output, ref offset);
        WriteJaggedArray(witness.Headers, headersLen, output, ref offset);
        WriteJaggedArray(witness.Keys, keysLen, output, ref offset);
        WriteJaggedArray(witness.State, stateLen, output, ref offset);

        Debug.Assert(offset == outputLen, "Invalid input length");

        return output;
    }

    public static (Block, Witness, uint) Deserialize(ReadOnlySpan<byte> input)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(input.Length, sizeof(int));

        var offset = 0;
        var chainId = ReadUInt32(input, ref offset);
        var blockLength = ReadInt32(input, ref offset);

        Rlp.ValueDecoderContext blockContext = new(input.Slice(offset, blockLength));
        Block block = _blockDecoder.Decode(ref blockContext, RlpBehaviors.None);
        blockContext.Check(blockLength);
        offset += blockLength;

        byte[][] codes = ReadJaggedArray(input, ref offset);
        byte[][] headers = ReadJaggedArray(input, ref offset);
        byte[][] keys = ReadJaggedArray(input, ref offset);
        byte[][] state = ReadJaggedArray(input, ref offset);

        Debug.Assert(offset == input.Length, "Invalid input length");

        Witness witness = new()
        {
            Codes = codes,
            Headers = headers,
            Keys = keys,
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
    private static uint ReadUInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(source.Slice(offset, sizeof(uint)));
        offset += sizeof(uint);

        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt32(uint value, Span<byte> destination, ref int offset)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, sizeof(uint)), value);
        offset += sizeof(uint);
    }

    private static byte[][] ReadJaggedArray(ReadOnlySpan<byte> source, ref int offset)
    {
        var sectionLen = ReadInt32(source, ref offset);

        if (sectionLen == 0)
            return [];

        var count = ReadInt32(source, ref offset);
        byte[][] output = new byte[count][];

        for (int i = 0; i < count; i++)
        {
            var len = ReadInt32(source, ref offset);

            output[i] = source.Slice(offset, len).ToArray();
            offset += len;
        }

        return output;
    }

    private static void WriteJaggedArray(
        ReadOnlySpan<byte[]> value,
        int sectionLength,
        Span<byte> destination,
        ref int offset)
    {
        WriteInt32(sectionLength, destination, ref offset);

        if (value.IsEmpty)
            return;

        var valueLen = value.Length;

        WriteInt32(valueLen, destination, ref offset);

        for (var i = 0; i < valueLen; i++)
        {
            var len = value[i].Length;

            WriteInt32(len, destination, ref offset);

            value[i].CopyTo(destination.Slice(offset, len));
            offset += len;
        }
    }

    private static int GetSerializedLength(ReadOnlySpan<byte[]> data)
    {
        if (data.IsEmpty)
            return 0;

        var len = sizeof(int);

        for (int i = 0; i < data.Length; i++)
            len += sizeof(int) + data[i].Length;

        return len;
    }
}
