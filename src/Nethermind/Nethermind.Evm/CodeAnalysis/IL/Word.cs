// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.CodeAnalysis.IL;

[StructLayout(LayoutKind.Explicit, Size = 32)]
internal struct Word
{
    public const int Size = 32;

    [FieldOffset(0)] public unsafe fixed byte _buffer[Size];

    [FieldOffset(Size - sizeof(byte))]
    public byte Byte0;

    [FieldOffset(Size - sizeof(int))]
    public int Int0;

    [FieldOffset(Size - sizeof(int))]
    public uint UInt0;

    [FieldOffset(Size - 2 * sizeof(int))]
    public uint UInt1;

    [FieldOffset(Size - 1 * sizeof(ulong))]
    public ulong Ulong0;

    [FieldOffset(Size - 2 * sizeof(ulong))]
    public ulong Ulong1;

    [FieldOffset(Size - 3 * sizeof(ulong))]
    public ulong Ulong2;

    [FieldOffset(Size - 4 * sizeof(ulong))]
    public ulong Ulong3;

    public bool IsZero => (Ulong0 | Ulong1 | Ulong2 | Ulong3) == 0;
    public void ToZero()
    {
        Ulong0 = 0; Ulong1 = 0;
        Ulong2 = 0; Ulong3 = 0;
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

    public unsafe Hash256 Hash256
    {
        get
        {
            fixed (byte* ptr = _buffer)
            {
                return new Hash256(new Span<byte>(ptr, 32));
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

    public unsafe ValueHash256 ValueHash256
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
            ulong u3 = Ulong3;
            ulong u2 = Ulong2;
            ulong u1 = Ulong1;
            ulong u0 = Ulong0;

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
                Ulong3 = BinaryPrimitives.ReverseEndianness(value.u3);
                Ulong2 = BinaryPrimitives.ReverseEndianness(value.u2);
                Ulong1 = BinaryPrimitives.ReverseEndianness(value.u1);
                Ulong0 = BinaryPrimitives.ReverseEndianness(value.u0);
            }
            else
            {
                Ulong3 = value.u3;
                Ulong2 = value.u2;
                Ulong1 = value.u1;
                Ulong0 = value.u0;
            }
        }
    }

    public static readonly FieldInfo Byte0Field = typeof(Word).GetField(nameof(Byte0));

    public static readonly FieldInfo Int0Field = typeof(Word).GetField(nameof(Int0));

    public static readonly FieldInfo UInt0Field = typeof(Word).GetField(nameof(UInt0));
    public static readonly FieldInfo UInt1Field = typeof(Word).GetField(nameof(UInt1));

    public static readonly FieldInfo Ulong0Field = typeof(Word).GetField(nameof(Ulong0));
    public static readonly FieldInfo Ulong1Field = typeof(Word).GetField(nameof(Ulong1));
    public static readonly FieldInfo Ulong2Field = typeof(Word).GetField(nameof(Ulong2));
    public static readonly FieldInfo Ulong3Field = typeof(Word).GetField(nameof(Ulong3));

    public static readonly MethodInfo GetIsZero = typeof(Word).GetProperty(nameof(IsZero))!.GetMethod;
    public static readonly MethodInfo SetToZero = typeof(Word).GetMethod(nameof(ToZero))!;

    public static readonly MethodInfo GetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.GetMethod;
    public static readonly MethodInfo SetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.SetMethod;

    public static readonly MethodInfo GetAddress = typeof(Word).GetProperty(nameof(Address))!.GetMethod;
    public static readonly MethodInfo SetAddress = typeof(Word).GetProperty(nameof(Address))!.SetMethod;

    public static readonly MethodInfo GetHash256 = typeof(Word).GetProperty(nameof(Hash256))!.GetMethod;
    public static readonly MethodInfo SetHash256 = typeof(Word).GetProperty(nameof(Hash256))!.SetMethod;

    public static readonly MethodInfo GetValueHash256 = typeof(Word).GetProperty(nameof(ValueHash256))!.GetMethod;
    public static readonly MethodInfo SetValueHash256 = typeof(Word).GetProperty(nameof(ValueHash256))!.SetMethod;

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
