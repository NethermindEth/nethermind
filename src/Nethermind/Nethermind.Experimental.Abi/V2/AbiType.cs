// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
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
        },
        Size = _ => 32,
    };

    public static readonly IAbi<UInt64> UInt64 = new()
    {
        Name = "uint64",
        Read = (ref BinarySpanReader r) => (UInt64)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt64 v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly IAbi<UInt32> UInt32 = new()
    {
        Name = "uint32",
        Read = (ref BinarySpanReader r) => (UInt32)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt32 v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly IAbi<UInt16> UInt16 = new()
    {
        Name = "uint16",
        Read = (ref BinarySpanReader r) => (UInt16)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt16 v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly IAbi<Byte> UInt8 = new()
    {
        Name = "uint8",
        Read = (ref BinarySpanReader r) => (Byte)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, Byte v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly IAbi<Boolean> Bool = new()
    {
        Name = "bool",
        Read = (ref BinarySpanReader r) => UInt256.Read(ref r) != 0,
        Write = (ref BinarySpanWriter w, Boolean v) => UInt256.Write(ref w, v ? 1u : 0u),
        Size = _ => 32,
    };

    public static IAbi<T[]> Array<T>(IAbi<T> elements) => new()
    {
        Name = $"{elements.Name}[]",
        Read = (ref BinarySpanReader r) => throw new NotImplementedException(),
        Write = (ref BinarySpanWriter w, T[] array) => throw new NotImplementedException(),
        Size = _ => throw new NotImplementedException(),
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
        },
        Size = array =>
        {
            if (array.Length != length) throw new AbiException();

            int size = 0;
            foreach (var item in array)
            {
                size += elements.Size(item);
            }

            return size;
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
        },
        Size = bytes =>
        {
            var lengthSize = 32;
            var bytesSize = Math.PadTo32(bytes.Length);

            return lengthSize + bytesSize;
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
        },
        Size = bytes =>
        {
            var bytesLength = Math.PadTo32(bytes.Length);

            return bytesLength;
        }
    };

    public static IAbi<String> String => new()
    {
        Name = $"string",
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r); // TODO: Use `UInt256` when dealing with lengths
            var bytes = r.ReadBytesPadded(length);
            return Encoding.UTF8.GetString(bytes);
        },
        Write = (ref BinarySpanWriter w, String v) =>
        {
            Span<byte> buffer = new byte[Encoding.UTF8.GetByteCount(v)];
            Encoding.UTF8.GetBytes(v, buffer);
            int length = buffer.Length;

            UInt256.Write(ref w, (UInt256)length);
            w.WritePadded(buffer);
        },
        Size = v =>
        {
            var lengthSize = 32;
            var byteCount = Encoding.UTF8.GetByteCount(v);
            var byteSize = Math.PadTo32(byteCount);

            return lengthSize + byteSize;
        }
    };
}

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
        },
        Size = v => abi.Size(v)
    };

    public static IAbi<(T1, T2)> Tuple<T1, T2>(IAbi<T1> abi1, IAbi<T2> abi2) => new()
    {
        Name = $"({abi1.Name},{abi2.Name})",
        Read = (ref BinarySpanReader r) =>
        {
            T1 arg1;
            if (abi1.IsDynamic)
            {
                var currentPosition = r.Position;
                UInt256 offset = UInt256.Read(ref r);
                r.Position = (int)offset;

                arg1 = abi1.Read(ref r);

                r.Position = currentPosition + 32;
            }
            else
            {
                arg1 = abi1.Read(ref r);
            }

            T2 arg2;
            if (abi2.IsDynamic)
            {
                var currentPosition = r.Position;
                UInt256 offset = UInt256.Read(ref r);
                r.Position = (int)offset;

                arg2 = abi2.Read(ref r);

                r.Position = currentPosition + 32;
            }
            else
            {
                arg2 = abi2.Read(ref r);
            }

            return (arg1, arg2);
        },
        Write = (ref BinarySpanWriter w, (T1, T2) v) =>
        {
            var ww = new BinarySpanWriter(w.Span[w.Position..]);

            Span<int> offsets = stackalloc int[2];
            if (abi1.IsDynamic)
            {
                offsets[0] = ww.Position;
                ww.Position += 32;
            }
            else
            {
                abi1.Write(ref ww, v.Item1);
            }

            if (abi2.IsDynamic)
            {
                offsets[1] = ww.Position;
                ww.Position += 32;
            }
            else
            {
                abi2.Write(ref ww, v.Item2);
            }

            if (abi1.IsDynamic)
            {
                var currentPosition = ww.Position;
                ww.Position = offsets[0];
                UInt256.Write(ref ww, (UInt256)currentPosition);
                ww.Position = currentPosition;

                abi1.Write(ref ww, v.Item1);
            }
            if (abi2.IsDynamic)
            {
                var currentPosition = ww.Position;
                ww.Position = offsets[1];
                UInt256.Write(ref ww, (UInt256)currentPosition);
                ww.Position = currentPosition;

                abi2.Write(ref ww, v.Item2);
            }

            w.Position += ww.Position;
        },
        Size = (v) =>
        {
            var size1 = (abi1.IsDynamic ? 32 : 0) + abi1.Size(v.Item1);
            var size2 = (abi2.IsDynamic ? 32 : 0) + abi2.Size(v.Item2);

            return size1 + size2;
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
        },
        Size = (v) => abi1.Size(v.Item1) + abi2.Size(v.Item2) + arg3.Size(v.Item3)
    };
}
