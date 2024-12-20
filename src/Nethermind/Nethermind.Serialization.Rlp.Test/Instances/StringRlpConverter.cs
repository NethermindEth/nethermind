// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;

namespace Nethermind.Serialization.Rlp.Test.Instances;

public abstract class StringRlpConverter : IRlpConverter<string>
{
    public static string Read(ref RlpReader reader)
    {
        ReadOnlySpan<byte> obj = reader.ReadBytes();
        return Encoding.UTF8.GetString(obj);
    }

    public static void Write(ref RlpWriter writer, string value)
    {
        ReadOnlySpan<byte> bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes);
    }
}

public static class StringRlpConverterExt
{
    public static string ReadString(this ref RlpReader reader) => StringRlpConverter.Read(ref reader);
    public static void Write(this ref RlpWriter writer, string value) => StringRlpConverter.Write(ref writer, value);
}
