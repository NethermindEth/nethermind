//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

using System;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using Nethermind.Int256;

namespace Nethermind.Evm.IL;

[StructLayout(LayoutKind.Explicit, Size = Size)]
struct Word
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

    public static readonly FieldInfo Byte0Field = typeof(Word).GetField(nameof(Byte0));

    public static readonly FieldInfo Int0Field = typeof(Word).GetField(nameof(Int0));

    public static readonly FieldInfo UInt0Field = typeof(Word).GetField(nameof(UInt0));
    public static readonly FieldInfo UInt1Field = typeof(Word).GetField(nameof(UInt1));

    public static readonly FieldInfo Ulong0Field = typeof(Word).GetField(nameof(Ulong0));
    public static readonly FieldInfo Ulong1Field = typeof(Word).GetField(nameof(Ulong1));
    public static readonly FieldInfo Ulong2Field = typeof(Word).GetField(nameof(Ulong2));
    public static readonly FieldInfo Ulong3Field = typeof(Word).GetField(nameof(Ulong3));

    public static readonly MethodInfo GetIsZero = typeof(Word).GetProperty(nameof(IsZero))!.GetMethod;

    public static readonly MethodInfo GetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.GetMethod;
    public static readonly MethodInfo SetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.SetMethod;

    public bool IsZero => (Ulong0 | Ulong1 | Ulong2 | Ulong3) == 0;

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
}
