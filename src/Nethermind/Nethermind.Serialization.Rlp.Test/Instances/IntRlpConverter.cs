// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.Serialization.Rlp.Test.Instances;

public abstract class IntRlpConverter : IRlpConverter<int>
{
    public static void Write(IRlpWriter writer, int value)
    {
        if (value < 0x80)
        {
            writer.WriteByte((byte)value);
        }
        else
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(bytes, value);
            bytes = bytes.TrimStart((byte)0);
            writer.Write(bytes);
        }
    }
}

public static class IntRlpConverterExt
{
    public static void Write(this IRlpWriter writer, int value) => IntRlpConverter.Write(writer, value);
}
