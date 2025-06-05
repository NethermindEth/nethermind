// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Experimental.Abi.V2;

// NOTE: Make `partial` to allow for extension
public static class AbiType
{
    public static readonly IAbi<UInt256> UInt256 = new()
    {
        Name = "uint256",
        Read = (ref BinarySpanReader r) =>
        {
            var bytes = r.ReadBytes(32);
            return new UInt256(bytes, isBigEndian: true);
        },
        Write = (ref BinarySpanWriter w, UInt256 v) =>
        {
            Span<byte> bytes = stackalloc byte[32];
            v.ToBigEndian(bytes);
            w.Write(bytes);
        }
    };

    public static readonly IAbi<UInt64> UInt64 = new()
    {
        Name = "uint64",
        Read = (ref BinarySpanReader r) => (UInt64)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt64 v) => UInt256.Write(ref w, v),
    };

    public static readonly IAbi<UInt32> UInt32 = new()
    {
        Name = "uint32",
        Read = (ref BinarySpanReader r) => (UInt32)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt32 v) => UInt256.Write(ref w, v),
    };

    public static readonly IAbi<UInt16> UInt16 = new()
    {
        Name = "uint16",
        Read = (ref BinarySpanReader r) => (UInt16)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt16 v) => UInt256.Write(ref w, v),
    };

    public static readonly IAbi<Byte> UInt8 = new()
    {
        Name = "uint8",
        Read = (ref BinarySpanReader r) => (Byte)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, Byte v) => UInt256.Write(ref w, v),
    };

    public static readonly IAbi<Boolean> Bool = new()
    {
        Name = "bool",
        Read = (ref BinarySpanReader r) => UInt256.Read(ref r) != 0,
        Write = (ref BinarySpanWriter w, Boolean v) => UInt256.Write(ref w, v ? 1u : 0u)
    };

    public static IAbi<T[]> Array<T>(IAbi<T> elements) => new()
    {
        Name = $"{elements.Name}[]",
        Read = (ref BinarySpanReader r) => throw new NotImplementedException(),
        Write = (ref BinarySpanWriter w, T[] array) => throw new NotImplementedException()
    };

    public static IAbi<T[]> Array<T>(IAbi<T> elements, int length) => new()
    {
        Name = $"{elements.Name}[{length}]",
        Read = (ref BinarySpanReader r) =>
        {
            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = elements.Read(ref r);
            }
            return array;
        },
        Write = (ref BinarySpanWriter w, T[] array) =>
        {
            if (array.Length != length) throw new AbiException();

            foreach (var item in array)
            {
                elements.Write(ref w, item);
            }
        }
    };

    public static IAbi<byte[]> Bytes => new()
    {
        Name = $"bytes",
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r); // TODO: Use `UInt256` when dealing with lengths
            return r.ReadBytesPadded(length).ToArray();
        },
        Write = (ref BinarySpanWriter w, byte[] bytes) =>
        {
            int length = bytes.Length;

            UInt256.Write(ref w, (UInt256)length);
            w.WritePadded(bytes);
        }
    };

    public static IAbi<byte[]> BytesM(int length) => new()
    {
        Name = $"bytes{length}",
        Read = (ref BinarySpanReader r) => r.ReadBytesPadded(length).ToArray(),
        Write = (ref BinarySpanWriter w, byte[] bytes) =>
        {
            if (bytes.Length != length) throw new AbiException();
            w.WritePadded(bytes);
        }
    };

    // Synonyms
    public static readonly IAbi<UInt256> UInt = UInt256;
}

internal static class BinaryReadWriterExtensions
{
    // TODO: Use `UInt256` when dealing with lengths
    private static int PadTo32(int length)
    {
        int rem = length % 32;
        return rem == 0 ? length : length + (32 - rem);
    }

    internal static void WritePadded(this BinaryWriter writer, byte[] bytes)
    {
        int length = bytes.Length;
        writer.Write(bytes);

        var padding = PadTo32(length) - length;
        if (padding > 0)
        {
            writer.Write(stackalloc byte[padding]);
        }
    }

    internal static byte[] ReadBytesPadded(this BinaryReader reader, int length)
    {
        var bytes = reader.ReadBytes(length);

        var padding = PadTo32(length) - length;
        if (padding > 0)
        {
            reader.ReadBytes(padding); // Skip padding bytes
        }

        return bytes;
    }
}
