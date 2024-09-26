// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Utilities;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Evm.CodeAnalysis.IL;

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct Word
{
    public const int Size = 32;

    [FieldOffset(0)] public unsafe fixed byte _buffer[Size];

    [FieldOffset(Size - sizeof(byte))]
    public byte _uByte0;

    [FieldOffset(Size - sizeof(int))]
    private int _sInt0;

    [FieldOffset(Size - sizeof(int))]
    private uint _uInt0;

    [FieldOffset(Size - 2 * sizeof(int))]
    private uint _uInt1;

    [FieldOffset(Size - 1 * sizeof(ulong))]
    private ulong _ulong0;

    [FieldOffset(Size - 2 * sizeof(ulong))]
    private ulong _ulong1;

    [FieldOffset(Size - 3 * sizeof(ulong))]
    private ulong _ulong2;

    [FieldOffset(Size - 4 * sizeof(ulong))]
    private ulong _ulong3;

    public bool IsZero => (_ulong0 | _ulong1 | _ulong2 | _ulong3) == 0;
    public void ToZero()
    {
        _ulong0 = 0; _ulong1 = 0;
        _ulong2 = 0; _ulong3 = 0;
    }

    public unsafe byte[] Array
    {
        get
        {
            byte[] array = new byte[32];
            fixed (byte* src = _buffer, dest = array)
            {
                Buffer.MemoryCopy(src, dest, 32, 32);
            }
            return array;
        }
        set
        {
            fixed (byte* src = value, dest = _buffer)
            {
                Buffer.MemoryCopy(src, dest + (32 - value.Length), value.Length, value.Length);
            }
        }
    }

    public unsafe ReadOnlySpan<byte> Span
    {
        get
        {
            fixed (byte* src = _buffer)
            {
                return new Span<byte>(src, 32);
            }
        }
        set
        {
            fixed (byte* src = value, dest = _buffer)
            {
                Buffer.MemoryCopy(src, dest + (32 - value.Length), value.Length, value.Length);
            }
        }
    }

    public unsafe ValueHash256 Keccak
    {
        get
        {
            fixed (byte* ptr = _buffer)
            {
                return new ValueHash256(new Span<byte>(ptr, 32));
            }
        }
        set
        {
            ReadOnlySpan<byte> buffer = value.Bytes;
            for (int i = 0; i < 20; i++)
            {
                _buffer[i] = buffer[i];
            }
        }
    }

    public unsafe Address Address
    {
        get
        {
            byte[] buffer = new byte[20];
            for (int i = 0; i < 20; i++)
            {
                buffer[i] = _buffer[i];
            }

            return new Address(buffer);
        }
        set
        {
            byte[] buffer = value.Bytes;
            for (int i = 0; i < 20; i++)
            {
                _buffer[i] = buffer[i];
            }
        }
    }

    public UInt256 UInt256
    {
        get
        {
            ulong u3 = _ulong3;
            ulong u2 = _ulong2;
            ulong u1 = _ulong1;
            ulong u0 = _ulong0;

            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(u3);
                u2 = BinaryPrimitives.ReverseEndianness(u2);
                u1 = BinaryPrimitives.ReverseEndianness(u1);
                u0 = BinaryPrimitives.ReverseEndianness(u0);
            }

            return new UInt256(u0, u1, u2, u3);
        }
        set
        {
            if (BitConverter.IsLittleEndian)
            {
                _ulong3 = BinaryPrimitives.ReverseEndianness(value.u3);
                _ulong2 = BinaryPrimitives.ReverseEndianness(value.u2);
                _ulong1 = BinaryPrimitives.ReverseEndianness(value.u1);
                _ulong0 = BinaryPrimitives.ReverseEndianness(value.u0);
            }
            else
            {
                _ulong3 = value.u3;
                _ulong2 = value.u2;
                _ulong1 = value.u1;
                _ulong0 = value.u0;
            }
        }
    }

    public uint UInt0
    {
        get
        {
            return _uInt0;
        }
        set
        {
            if (BitConverter.IsLittleEndian)
            {
                _uInt0 = BinaryPrimitives.ReverseEndianness(value);
            }
            else
            {
                _uInt0 = value;
            }
        }
    }

    public int Int0
    {
        get
        {
            return _sInt0;
        }
        set
        {
            if (BitConverter.IsLittleEndian)
            {
                _sInt0 = BinaryPrimitives.ReverseEndianness(value);
            }
            else
            {
                _sInt0 = value;
            }
        }
    }

    public ulong ULong0
    {
        get
        {
            return _ulong0;
        }
        set
        {
            if (BitConverter.IsLittleEndian)
            {
                _ulong0 = BinaryPrimitives.ReverseEndianness(value);
            }
            else
            {
                _ulong0 = value;
            }
        }
    }


    public unsafe long LeadingZeros
    {
        get
        {
            fixed (byte* ptr = _buffer)
            {
                byte* end = ptr + 32;
                byte* current = ptr;
                while (current < end && *current == 0)
                {
                    current++;
                }

                return current - ptr;
            }
        }
    }

    public static readonly MethodInfo LeadingZeroProp = typeof(Word).GetProperty(nameof(LeadingZeros))!.GetMethod;
    public static readonly FieldInfo Byte0Field = typeof(Word).GetField(nameof(_uByte0));

    public static readonly MethodInfo GetInt0 = typeof(Word).GetProperty(nameof(Int0))!.GetMethod;
    public static readonly MethodInfo SetInt0 = typeof(Word).GetProperty(nameof(Int0))!.SetMethod;

    public static readonly MethodInfo GetUInt0 = typeof(Word).GetProperty(nameof(UInt0))!.GetMethod;
    public static readonly MethodInfo SetUInt0 = typeof(Word).GetProperty(nameof(UInt0))!.SetMethod;

    public static readonly MethodInfo GetULong0 = typeof(Word).GetProperty(nameof(ULong0))!.GetMethod;
    public static readonly MethodInfo SetULong0 = typeof(Word).GetProperty(nameof(ULong0))!.SetMethod;

    public static readonly MethodInfo GetIsZero = typeof(Word).GetProperty(nameof(IsZero))!.GetMethod;
    public static readonly MethodInfo SetToZero = typeof(Word).GetMethod(nameof(ToZero))!;

    public static readonly MethodInfo GetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.GetMethod;
    public static readonly MethodInfo SetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.SetMethod;

    public static readonly MethodInfo GetAddress = typeof(Word).GetProperty(nameof(Address))!.GetMethod;
    public static readonly MethodInfo SetAddress = typeof(Word).GetProperty(nameof(Address))!.SetMethod;

    public static readonly MethodInfo GetKeccak = typeof(Word).GetProperty(nameof(Keccak))!.GetMethod;
    public static readonly MethodInfo SetKeccak = typeof(Word).GetProperty(nameof(Keccak))!.SetMethod;

    public static readonly MethodInfo GetArray = typeof(Word).GetProperty(nameof(Array))!.GetMethod;
    public static readonly MethodInfo SetArray = typeof(Word).GetProperty(nameof(Array))!.SetMethod;

    public static readonly MethodInfo GetSpan = typeof(Word).GetProperty(nameof(Span))!.GetMethod;
    public static readonly MethodInfo SetSpan = typeof(Word).GetProperty(nameof(Span))!.SetMethod;

    public static explicit operator Word(Span<byte> span)
    {
        unsafe
        {
            var result = new Word();
            result.Span = span;
            return result;
        }
    }

}
