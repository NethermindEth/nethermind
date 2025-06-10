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
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
            return r.Scoped((ref BinarySpanReader r) =>
            {
                var array = new T[length];
                if (elements.IsDynamic)
                {
                    int read = 0;
                    for (int i = 0; i < length; i++)
                    {
                        (array[i], read) = r.ReadOffset((ref BinarySpanReader r) => elements.Read(ref r));
                    }
                    r.Advance(read);
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        array[i] = elements.Read(ref r);
                    }
                }

                return array;
            });
        },
        Write = (ref BinarySpanWriter w, T[] array) =>
        {
            UInt256.Write(ref w, (UInt256)array.Length);
            w.Scoped((ref BinarySpanWriter w) =>
            {
                if (elements.IsDynamic)
                {
                    w.Advance(array.Length * 32);

                    for (int i = 0; i < array.Length; i++)
                    {
                        w.WriteOffset(i * 32, (ref BinarySpanWriter w) => elements.Write(ref w, array[i]));
                    }
                }
                else
                {
                    for (int i = 0; i < array.Length; i++)
                    {
                        elements.Write(ref w, array[i]);
                    }
                }
            });
        },
        Size = array =>
        {
            var offsetSize = 32;
            var lengthSize = 32;
            var elementsSize = array.Sum(e => elements.Size(e));

            return offsetSize + lengthSize + elementsSize;
        },
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
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
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
            var offsetSize = 32;
            var lengthSize = 32;
            var bytesSize = Math.PadTo32(bytes.Length);

            return offsetSize + lengthSize + bytesSize;
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
            int length = (int)UInt256.Read(ref r);
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
        Size = (v) =>
        {
            var offsetSize = 32;
            var lengthSize = 32;
            var byteCount = Encoding.UTF8.GetByteCount(v);
            var byteSize = Math.PadTo32(byteCount);

            return offsetSize + lengthSize + byteSize;
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
        IsDynamic = abi.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((ref BinarySpanReader r) =>
            {
                T arg;
                if (abi.IsDynamic)
                {
                    (arg, int read) = r.ReadOffset((ref BinarySpanReader r) => abi.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg = abi.Read(ref r);
                }

                return arg;
            });
        },
        Write = (ref BinarySpanWriter w, T v) =>
        {
            w.Scoped((ref BinarySpanWriter w) =>
            {
                if (abi.IsDynamic)
                {
                    int offset = w.Advance(32);
                    w.WriteOffset(offset, (ref BinarySpanWriter w) => abi.Write(ref w, v));
                }
                else
                {
                    abi.Write(ref w, v);
                }
            });
        },
        Size = (v) => abi.Size(v)
    };

    // TODO: Investigate if we can generalize this code to avoid duplication when dealing with tuples of different sizes
    public static IAbi<(T1, T2)> Tuple<T1, T2>(IAbi<T1> abi1, IAbi<T2> abi2) => new()
    {
        Name = $"({abi1.Name},{abi2.Name})",
        IsDynamic = abi1.IsDynamic || abi2.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((ref BinarySpanReader r) =>
            {
                T1 arg1;
                if (abi1.IsDynamic)
                {
                    (arg1, _) = r.ReadOffset((ref BinarySpanReader r) => abi1.Read(ref r));
                }
                else
                {
                    arg1 = abi1.Read(ref r);
                }

                T2 arg2;
                if (abi2.IsDynamic)
                {
                    (arg2, int read) = r.ReadOffset((ref BinarySpanReader r) => abi2.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg2 = abi2.Read(ref r);
                }

                return (arg1, arg2);
            });
        },
        Write = (ref BinarySpanWriter w, (T1, T2) v) =>
        {
            w.Scoped((ref BinarySpanWriter w) =>
            {
                Span<int> offsets = stackalloc int[2];
                if (abi1.IsDynamic)
                {
                    offsets[0] = w.Advance(32);
                }
                else
                {
                    abi1.Write(ref w, v.Item1);
                }

                if (abi2.IsDynamic)
                {
                    offsets[1] = w.Advance(32);
                }
                else
                {
                    abi2.Write(ref w, v.Item2);
                }

                if (abi1.IsDynamic)
                {
                    w.WriteOffset(offsets[0], (ref BinarySpanWriter w) => abi1.Write(ref w, v.Item1));
                }
                if (abi2.IsDynamic)
                {
                    w.WriteOffset(offsets[1], (ref BinarySpanWriter w) => abi2.Write(ref w, v.Item2));
                }
            });
        },
        Size = (v) => abi1.Size(v.Item1) + abi2.Size(v.Item2)
    };

    public static IAbi<(T1, T2, T3)> Tuple<T1, T2, T3>(IAbi<T1> abi1, IAbi<T2> abi2, IAbi<T3> abi3) => new()
    {
        Name = $"({abi1.Name},{abi2.Name},{abi3.Name})",
        IsDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((ref BinarySpanReader r) =>
            {
                T1 arg1;
                if (abi1.IsDynamic)
                {
                    (arg1, _) = r.ReadOffset((ref BinarySpanReader r) => abi1.Read(ref r));
                }
                else
                {
                    arg1 = abi1.Read(ref r);
                }

                T2 arg2;
                if (abi2.IsDynamic)
                {
                    (arg2, int read) = r.ReadOffset((ref BinarySpanReader r) => abi2.Read(ref r));
                }
                else
                {
                    arg2 = abi2.Read(ref r);
                }

                T3 arg3;
                if (abi3.IsDynamic)
                {
                    (arg3, int read) = r.ReadOffset((ref BinarySpanReader r) => abi3.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg3 = abi3.Read(ref r);
                }

                return (arg1, arg2, arg3);
            });
        },
        Write = (ref BinarySpanWriter w, (T1, T2, T3) v) =>
        {
            w.Scoped((ref BinarySpanWriter w) =>
            {
                Span<int> offsets = stackalloc int[3];
                if (abi1.IsDynamic)
                {
                    offsets[0] = w.Advance(32);
                }
                else
                {
                    abi1.Write(ref w, v.Item1);
                }

                if (abi2.IsDynamic)
                {
                    offsets[1] = w.Advance(32);
                }
                else
                {
                    abi2.Write(ref w, v.Item2);
                }

                if (abi3.IsDynamic)
                {
                    offsets[2] = w.Advance(32);
                }
                else
                {
                    abi3.Write(ref w, v.Item3);
                }

                if (abi1.IsDynamic)
                {
                    w.WriteOffset(offsets[0], (ref BinarySpanWriter w) => abi1.Write(ref w, v.Item1));
                }
                if (abi2.IsDynamic)
                {
                    w.WriteOffset(offsets[1], (ref BinarySpanWriter w) => abi2.Write(ref w, v.Item2));
                }
                if (abi3.IsDynamic)
                {
                    w.WriteOffset(offsets[2], (ref BinarySpanWriter w) => abi3.Write(ref w, v.Item3));
                }
            });
        },
        Size = (v) => abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3)
    };

    public static IAbi<(T1, T2, T3, T4)> Tuple<T1, T2, T3, T4>(IAbi<T1> abi1, IAbi<T2> abi2, IAbi<T3> abi3, IAbi<T4> abi4) => new()
    {
        Name = $"({abi1.Name},{abi2.Name},{abi3.Name},{abi4.Name})",
        IsDynamic = abi1.IsDynamic || abi2.IsDynamic || abi3.IsDynamic || abi4.IsDynamic,
        Read = (ref BinarySpanReader r) =>
        {
            return r.Scoped((ref BinarySpanReader r) =>
            {
                T1 arg1;
                if (abi1.IsDynamic)
                {
                    (arg1, _) = r.ReadOffset((ref BinarySpanReader r) => abi1.Read(ref r));
                }
                else
                {
                    arg1 = abi1.Read(ref r);
                }

                T2 arg2;
                if (abi2.IsDynamic)
                {
                    (arg2, _) = r.ReadOffset((ref BinarySpanReader r) => abi2.Read(ref r));
                }
                else
                {
                    arg2 = abi2.Read(ref r);
                }

                T3 arg3;
                if (abi3.IsDynamic)
                {
                    (arg3, _) = r.ReadOffset((ref BinarySpanReader r) => abi3.Read(ref r));
                }
                else
                {
                    arg3 = abi3.Read(ref r);
                }

                T4 arg4;
                if (abi4.IsDynamic)
                {
                    (arg4, int read) = r.ReadOffset((ref BinarySpanReader r) => abi4.Read(ref r));
                    r.Advance(read);
                }
                else
                {
                    arg4 = abi4.Read(ref r);
                }

                return (arg1, arg2, arg3, arg4);
            });
        },
        Write = (ref BinarySpanWriter w, (T1, T2, T3, T4) v) =>
        {
            w.Scoped((ref BinarySpanWriter w) =>
            {
                Span<int> offsets = stackalloc int[4];
                if (abi1.IsDynamic)
                {
                    offsets[0] = w.Advance(32);
                }
                else
                {
                    abi1.Write(ref w, v.Item1);
                }

                if (abi2.IsDynamic)
                {
                    offsets[1] = w.Advance(32);
                }
                else
                {
                    abi2.Write(ref w, v.Item2);
                }

                if (abi3.IsDynamic)
                {
                    offsets[2] = w.Advance(32);
                }
                else
                {
                    abi3.Write(ref w, v.Item3);
                }
                if (abi4.IsDynamic)
                {
                    offsets[3] = w.Advance(32);
                }
                else
                {
                    abi4.Write(ref w, v.Item4);
                }

                if (abi1.IsDynamic)
                {
                    w.WriteOffset(offsets[0], (ref BinarySpanWriter w) => abi1.Write(ref w, v.Item1));
                }
                if (abi2.IsDynamic)
                {
                    w.WriteOffset(offsets[1], (ref BinarySpanWriter w) => abi2.Write(ref w, v.Item2));
                }
                if (abi3.IsDynamic)
                {
                    w.WriteOffset(offsets[2], (ref BinarySpanWriter w) => abi3.Write(ref w, v.Item3));
                }
                if (abi4.IsDynamic)
                {
                    w.WriteOffset(offsets[3], (ref BinarySpanWriter w) => abi4.Write(ref w, v.Item4));
                }
            });
        },
        Size = (v) => abi1.Size(v.Item1) + abi2.Size(v.Item2) + abi3.Size(v.Item3) + abi4.Size(v.Item4)
    };
}
