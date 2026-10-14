// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Int256;

namespace Nethermind.Experimental.Abi;

public static partial class AbiType
{
    public static readonly Abi<UInt256> UInt256 = new()
    {
        Name = "uint256",
        Read = (ref BinarySpanReader r) =>
        {
            ReadOnlySpan<byte> bytes = r.ReadBytes(32);
            return new UInt256(bytes, isBigEndian: true);
        },
        Write = (ref BinarySpanWriter w, UInt256 v) =>
        {
            Span<byte> output = w.Take(32);
            v.ToBigEndian(output);
        },
        Size = _ => 32,
    };

    public static readonly Abi<UInt64> UInt64 = new()
    {
        Name = "uint64",
        Read = (ref BinarySpanReader r) => (UInt64)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt64 v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly Abi<UInt32> UInt32 = new()
    {
        Name = "uint32",
        Read = (ref BinarySpanReader r) => (UInt32)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt32 v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly Abi<UInt16> UInt16 = new()
    {
        Name = "uint16",
        Read = (ref BinarySpanReader r) => (UInt16)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, UInt16 v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly Abi<Byte> UInt8 = new()
    {
        Name = "uint8",
        Read = (ref BinarySpanReader r) => (Byte)UInt256.Read(ref r),
        Write = (ref BinarySpanWriter w, Byte v) => UInt256.Write(ref w, v),
        Size = _ => 32,
    };

    public static readonly Abi<Boolean> Bool = new()
    {
        Name = "bool",
        Read = (ref BinarySpanReader r) => UInt256.Read(ref r) != 0,
        Write = (ref BinarySpanWriter w, Boolean v) => UInt256.Write(ref w, v ? 1u : 0u),
        Size = _ => 32,
    };

    public static Abi<T[]> Array<T>(Abi<T> abi) => new()
    {
        Name = $"{abi.Name}[]",
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
            return r.Scoped((length, abi), static ((int, Abi<T>) ctx, ref BinarySpanReader r) =>
            {
                (int length, Abi<T> abi) = ctx;

                T[] array = new T[length];
                if (abi.IsDynamic)
                {
                    int read = 0;
                    for (int i = 0; i < length; i++)
                    {
                        (array[i], read) = r.ReadOffset(abi, static (Abi<T> abi, ref BinarySpanReader r) => abi.Read(ref r));
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
            w.Scoped((array, abi), static ((T[], Abi<T>) ctx, ref BinarySpanWriter w) =>
            {
                (T[] array, Abi<T> abi) = ctx;

                if (abi.IsDynamic)
                {
                    w.Advance(array.Length * 32);

                    for (int i = 0; i < array.Length; i++)
                    {
                        w.WriteOffset(i * 32, (array[i], abi), static ((T, Abi<T>) ctx, ref BinarySpanWriter w) =>
                        {
                            (T e, Abi<T> abi) = ctx;
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

            int elementsSize = 0;
            foreach (T e in array)
            {
                elementsSize += abi.Size(e);
            }

            return offsetSize + lengthSize + elementsSize;
        }
    };

    public static Abi<T[]> Array<T>(Abi<T> abi, int length) => new()
    {
        Name = $"{abi.Name}[{length}]",
        Read = (ref BinarySpanReader r) =>
        {
            T[] array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = abi.Read(ref r);
            }

            return array;
        },
        Write = (ref BinarySpanWriter w, T[] array) =>
        {
            if (array.Length != length) throw new AbiException();

            foreach (T item in array)
            {
                abi.Write(ref w, item);
            }
        },
        Size = array =>
        {
            if (array.Length != length) throw new AbiException();

            int size = 0;
            foreach (T item in array)
            {
                size += abi.Size(item);
            }

            return size;
        }
    };

    public static Abi<byte[]> Bytes => new()
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
            int bytesSize = Math.PadTo32(bytes.Length);

            return offsetSize + lengthSize + bytesSize;
        }
    };

    public static Abi<byte[]> BytesM(int length) => new()
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
            int bytesLength = Math.PadTo32(bytes.Length);

            return bytesLength;
        }
    };

    public static Abi<String> String => new()
    {
        Name = "string",
        IsDynamic = true,
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
            ReadOnlySpan<byte> bytes = r.ReadBytesPadded(length);
            return Encoding.UTF8.GetString(bytes);
        },
        Write = (ref BinarySpanWriter w, String v) =>
        {
            int byteCount = Encoding.UTF8.GetByteCount(v);
            int byteSize = Math.PadTo32(byteCount);

            UInt256.Write(ref w, (UInt256)byteCount);

            Span<byte> buffer = w.Take(byteSize);
            Encoding.UTF8.GetBytes(v, buffer);
        },
        Size = v =>
        {
            const int offsetSize = 32;
            const int lengthSize = 32;
            int byteCount = Encoding.UTF8.GetByteCount(v);
            int byteSize = Math.PadTo32(byteCount);

            return offsetSize + lengthSize + byteSize;
        }
    };

    // Synonyms
    public static readonly Abi<UInt256> UInt = UInt256;
}
