// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Serialization.Rlp.Test.Instances;

// Spiritually implements `IRlpConverter` but due to restrictions on `ReadOnlySpan` we cannot make it explicit
public abstract class ReadOnlySpanConverter /* : IRlpConverter<ReadOnlySpan<byte>> */
{
    public static void Write(IRlpWriter writer, ReadOnlySpan<byte> value)
    {
        // "If a string is 0-55 bytes long, the RLP encoding consists of
        //      - a single byte with value 0x80 (dec. 128)
        //      - plus the length of the string
        //      - followed by the string.
        // The range of the first byte is thus [0x80, 0xb7] (dec. [128, 183])".
        if (value.Length < 55)
        {
            writer.WriteByte((byte)(0x80 + value.Length));
        }
        // If a string is more than 55 bytes long, the RLP encoding consists of
        //      - a single byte with value 0xb7 (dec. 183)
        //      - plus the length in bytes of the length of the string in binary form,
        //      - followed by the length of the string,
        //      - followed by the string.
        // The range of the first byte is thus [0xb8, 0xbf] (dec. [184, 191]).
        else
        {
            Span<byte> binaryLength = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(binaryLength, value.Length);
            binaryLength = binaryLength.TrimStart((byte)0);
            writer.WriteByte((byte)(0xB7 + binaryLength.Length));
            writer.WriteBytes(binaryLength);
        }

        writer.WriteBytes(value);
    }
}

public static class ReadOnlySpanConverterExt
{
    public static void Write(this IRlpWriter writer, ReadOnlySpan<byte> value) => ReadOnlySpanConverter.Write(writer, value);
}
