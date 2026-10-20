// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Text;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
    };

    public static readonly Abi<Int256.Int256> Int256 = new()
    {
        Name = "int256",
        Read = (ref BinarySpanReader r) => new Int256.Int256(r.ReadBytes(32), isBigEndian: true),
        Write = (ref BinarySpanWriter w, Int256.Int256 v) => ((UInt256)v).ToBigEndian(w.Take(32)),
    };

    public static Abi<T> UIntN<T>() where T : IBinaryInteger<T>, IUnsignedNumber<T> => new()
    {
        Name = $"uint{T.Zero.GetByteCount() * 8}",
        Read = (ref BinarySpanReader r) =>
        {
            if (!T.TryReadBigEndian(r.ReadBytes(32), isUnsigned: true, out T value)) throw new AbiException();
            return value;
        },
        Write = (ref BinarySpanWriter w, T value) =>
        {
            Span<byte> word = w.Take(32);
            int size = value.GetByteCount();
            word[..^size].Clear();
            value.TryWriteBigEndian(word[^size..], out _);
        },
    };

    public static Abi<T> IntN<T>() where T : IBinaryInteger<T>, ISignedNumber<T> => new()
    {
        Name = $"int{T.Zero.GetByteCount() * 8}",
        Read = (ref BinarySpanReader r) =>
        {
            if (!T.TryReadBigEndian(r.ReadBytes(32), isUnsigned: false, out T value)) throw new AbiException();
            return value;
        },
        Write = (ref BinarySpanWriter w, T value) =>
        {
            Span<byte> word = w.Take(32);
            int size = value.GetByteCount();
            word[..^size].Fill(T.IsNegative(value) ? (byte)0xff : (byte)0);
            value.TryWriteBigEndian(word[^size..], out _);
        },
    };

    public static readonly Abi<UInt64> UInt64 = UIntN<UInt64>();
    public static readonly Abi<UInt32> UInt32 = UIntN<UInt32>();
    public static readonly Abi<UInt16> UInt16 = UIntN<UInt16>();
    public static readonly Abi<Byte> UInt8 = UIntN<Byte>();

    public static readonly Abi<Int64> Int64 = IntN<Int64>();
    public static readonly Abi<Int32> Int32 = IntN<Int32>();
    public static readonly Abi<Int16> Int16 = IntN<Int16>();
    public static readonly Abi<SByte> Int8 = IntN<SByte>();

    public static readonly Abi<Boolean> Bool = new()
    {
        Name = "bool",
        Read = (ref BinarySpanReader r) =>
        {
            UInt256 value = UInt256.Read(ref r);
            if (value > 1) throw new AbiException();
            return !value.IsZero;
        },
        Write = (ref BinarySpanWriter w, Boolean v) => UInt256.Write(ref w, v ? 1u : 0u),
    };

    public static readonly Abi<Address> Address = new()
    {
        Name = "address",
        Read = (ref BinarySpanReader r) =>
        {
            ReadOnlySpan<byte> word = r.ReadBytes(32);
            if (word[..12].ContainsAnyExcept((byte)0)) throw new AbiException();
            return new Address(word[12..]);
        },
        Write = (ref BinarySpanWriter w, Address v) =>
        {
            Span<byte> word = w.Take(32);
            word[..12].Clear();
            v.Bytes.CopyTo(word[12..]);
        },
    };

    public static Abi<T[]> Array<T>(Abi<T> abi) => new()
    {
        Name = $"{abi.Name}[]",
        Read = (ref BinarySpanReader r) =>
        {
            UInt256 length = UInt256.Read(ref r);
            // Empty element heads (`T[0]`) are bounded as a word so that allocations stay proportional to the input
            if (length > (UInt256)(r.Remaining / int.Max(abi.HeadSize, 32))) throw new AbiException();

            return r.ReadArray(abi, (int)length);
        },
        Write = (ref BinarySpanWriter w, T[] array) =>
        {
            UInt256.Write(ref w, (UInt256)array.Length);
            w.WriteArray(abi, array);
        },
        TailSize = array =>
        {
            const int lengthSize = 32;
            if (!abi.IsDynamic) return lengthSize + array.Length * abi.HeadSize;

            int size = lengthSize;
            foreach (T e in array)
            {
                size += 32 + abi.Size(e);
            }

            return size;
        }
    };

    public static Abi<T[]> Array<T>(Abi<T> abi, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        int headSize = checked(length * abi.HeadSize);

        return new()
        {
            Name = $"{abi.Name}[{length}]",
            Read = (ref BinarySpanReader r) => r.ReadArray(abi, length),
            Write = (ref BinarySpanWriter w, T[] array) =>
            {
                if (array.Length != length) throw new AbiException();
                w.WriteArray(abi, array);
            },
            HeadSize = abi.IsDynamic ? 32 : headSize,
            TailSize = !abi.IsDynamic ? null : array =>
            {
                int size = 0;
                foreach (T e in array)
                {
                    size += 32 + abi.Size(e);
                }

                return size;
            }
        };
    }

    public static readonly Abi<byte[]> Bytes = new()
    {
        Name = "bytes",
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
        TailSize = bytes =>
        {
            const int lengthSize = 32;
            int bytesSize = Math.PadTo32(bytes.Length);

            return lengthSize + bytesSize;
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
        HeadSize = Math.PadTo32(length)
    };

    public static readonly Abi<ValueHash256> Bytes32 = new()
    {
        Name = "bytes32",
        Read = (ref BinarySpanReader r) => new ValueHash256(r.ReadBytes(32)),
        Write = (ref BinarySpanWriter w, ValueHash256 v) => v.Bytes.CopyTo(w.Take(32)),
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static readonly Abi<String> String = new()
    {
        Name = "string",
        Read = (ref BinarySpanReader r) =>
        {
            int length = (int)UInt256.Read(ref r);
            ReadOnlySpan<byte> bytes = r.ReadBytesPadded(length);
            return StrictUtf8.GetString(bytes);
        },
        Write = (ref BinarySpanWriter w, String v) =>
        {
            int byteCount = StrictUtf8.GetByteCount(v);
            int byteSize = Math.PadTo32(byteCount);

            UInt256.Write(ref w, (UInt256)byteCount);

            Span<byte> buffer = w.Take(byteSize);
            StrictUtf8.GetBytes(v, buffer);
            buffer[byteCount..].Clear();
        },
        TailSize = v =>
        {
            const int lengthSize = 32;
            int byteCount = StrictUtf8.GetByteCount(v);
            int byteSize = Math.PadTo32(byteCount);

            return lengthSize + byteSize;
        }
    };

    // Synonyms
    public static readonly Abi<UInt256> UInt = UInt256;
    public static readonly Abi<Int256.Int256> Int = Int256;
}
