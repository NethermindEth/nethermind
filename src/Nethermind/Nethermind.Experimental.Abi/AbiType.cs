// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Text;
using Nethermind.Int256;

namespace Nethermind.Experimental.Abi;

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
            var output = w.Take(32);
            v.ToBigEndian(output);
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

    public static IAbi<T[]> Array<T>(IAbi<T> abi) => new()
    {
        Name = $"{abi.Name}[]",
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
            return r.Scoped((length, abi), static ((int, IAbi<T>) ctx, ref BinarySpanReader r) =>
            {
                var (length, abi) = ctx;

                var array = new T[length];
                if (abi.IsDynamic)
                {
                    int read = 0;
                    for (int i = 0; i < length; i++)
                    {
                        (array[i], read) = r.ReadOffset(abi, static (IAbi<T> abi, ref BinarySpanReader r) => abi.Read(ref r));
                    }
                    r.Advance(read);
                }
                else
                {
                    for (int i = 0; i < length; i++)
                    {
                        array[i] = abi.Read(ref r);
                    }
                }

                return array;
            });
        },
        Write = (ref BinarySpanWriter w, T[] array) =>
        {
            UInt256.Write(ref w, (UInt256)array.Length);
            w.Scoped((array, abi), static ((T[], IAbi<T>) ctx, ref BinarySpanWriter w) =>
            {
                var (array, abi) = ctx;

                if (abi.IsDynamic)
                {
                    w.Advance(array.Length * 32);

                    for (int i = 0; i < array.Length; i++)
                    {
                        w.WriteOffset(i * 32, (array[i], abi), static ((T, IAbi<T>) ctx, ref BinarySpanWriter w) =>
                        {
                            var (e, abi) = ctx;
                            abi.Write(ref w, e);
                        });
                    }
                }
                else
                {
                    for (int i = 0; i < array.Length; i++)
                    {
                        abi.Write(ref w, array[i]);
                    }
                }
            });
        },
        Size = array =>
        {
            const int offsetSize = 32;
            const int lengthSize = 32;

            var elementsSize = 0;
            foreach (var e in array)
            {
                elementsSize += abi.Size(e);
            }

            return offsetSize + lengthSize + elementsSize;
        }
    };

    public static IAbi<T[]> Array<T>(IAbi<T> abi, int length) => new()
    {
        Name = $"{abi.Name}[{length}]",
        Read = (ref BinarySpanReader r) =>
        {
            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = abi.Read(ref r);
            }

            return array;
        },
        Write = (ref BinarySpanWriter w, T[] array) =>
        {
            if (array.Length != length) throw new AbiException();

            foreach (var item in array)
            {
                abi.Write(ref w, item);
            }
        },
        Size = array =>
        {
            if (array.Length != length) throw new AbiException();

            int size = 0;
            foreach (var item in array)
            {
                size += abi.Size(item);
            }

            return size;
        }
    };

    public static IAbi<byte[]> Bytes => new()
    {
        Name = "bytes",
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
            const int offsetSize = 32;
            const int lengthSize = 32;
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
        Name = "string",
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
            var bytes = r.ReadBytesPadded(length);
            return Encoding.UTF8.GetString(bytes);
        },
        Write = (ref BinarySpanWriter w, String v) =>
        {
            int byteCount = Encoding.UTF8.GetByteCount(v);
            int byteSize = Math.PadTo32(byteCount);

            UInt256.Write(ref w, (UInt256)byteCount);

            var buffer = w.Take(byteSize);
            Encoding.UTF8.GetBytes(v, buffer);
        },
        Size = v =>
        {
            const int offsetSize = 32;
            const int lengthSize = 32;
            var byteCount = Encoding.UTF8.GetByteCount(v);
            var byteSize = Math.PadTo32(byteCount);

            return offsetSize + lengthSize + byteSize;
        }
    };

    // Synonyms
    public static readonly IAbi<UInt256> UInt = UInt256;
}
