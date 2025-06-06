// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Experimental.Abi.V2;

public static partial class AbiType
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
public static partial class AbiType
{
    public static readonly IAbi<UInt256> UInt = UInt256;
}

// Tuples
public static partial class AbiType
{

    public static IAbi<T> Tuple<T>(IAbi<T> abi) => new()
    {
        Name = $"({abi})",
        Read = (ref BinarySpanReader r) =>
        {
            T arg = abi.Read(ref r);
            return arg;
        },
        Write = (ref BinarySpanWriter w, T v) =>
        {
            abi.Write(ref w, v);
        }
    };

    public static IAbi<(T1, T2)> Tuple<T1, T2>(IAbi<T1> abi1, IAbi<T2> abi2) => new()
    {
        Name = $"({abi1.Name},{abi2.Name})",
        Read = (ref BinarySpanReader r) =>
        {
            T1 arg1 = abi1.Read(ref r);
            T2 arg2 = abi2.Read(ref r);
            return (arg1, arg2);
        },
        Write = (ref BinarySpanWriter w, (T1, T2) v) =>
        {
            abi1.Write(ref w, v.Item1);
            abi2.Write(ref w, v.Item2);
        }
    };

    public static IAbi<(T1, T2, T3)> Tuple<T1, T2, T3>(IAbi<T1> abi1, IAbi<T2> abi2, IAbi<T3> arg3) => new()
    {
        Name = $"({abi1.Name},{abi2.Name},{arg3.Name})",
        Read = (ref BinarySpanReader r) =>
        {
            T1 arg1 = abi1.Read(ref r);
            T2 arg2 = abi2.Read(ref r);
            T3 arg3Value = arg3.Read(ref r);
            return (arg1, arg2, arg3Value);
        },
        Write = (ref BinarySpanWriter w, (T1, T2, T3) v) =>
        {
            abi1.Write(ref w, v.Item1);
            abi2.Write(ref w, v.Item2);
            arg3.Write(ref w, v.Item3);
        }
    };
}
