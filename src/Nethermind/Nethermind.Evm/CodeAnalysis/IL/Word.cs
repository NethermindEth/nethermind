// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Threading.Tasks;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Nethermind.Evm.CodeAnalysis.IL;

[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct Word
{
    public const int Size = 32;
    public const int FullSize = 256;

    [FieldOffset(0)] public unsafe fixed byte _buffer[Size];

    [FieldOffset(Size - sizeof(byte))] public byte _uByte0;

    [FieldOffset(Size - sizeof(int))] private int _sInt0;

    [FieldOffset(Size - sizeof(int))] private uint _uInt0;

    [FieldOffset(Size - 2 * sizeof(int))] private uint _uInt1;

    [FieldOffset(Size - 1 * sizeof(ulong))]
    private ulong _ulong0;

    [FieldOffset(Size - 2 * sizeof(ulong))]
    private ulong _ulong1;

    [FieldOffset(Size - 3 * sizeof(ulong))]
    private ulong _ulong2;

    [FieldOffset(Size - 4 * sizeof(ulong))]
    private ulong _ulong3;

    public bool CheckIfEqual(ref Word other) => _ulong0 == other._ulong0 && _ulong1 == other._ulong1 &&
                                                _ulong2 == other._ulong2 && _ulong3 == other._ulong3;

    public bool IsZero => (_ulong0 | _ulong1 | _ulong2 | _ulong3) == 0;
    public bool IsOne => (_ulong1 | _ulong2 | _ulong3) == 0 && (_ulong0 == 0x0100000000000000);

    public bool IsMinusOne => _ulong1 == ulong.MaxValue && _ulong2 == ulong.MaxValue && _ulong3 == ulong.MaxValue &&
                              _ulong0 == ulong.MaxValue;

    public bool IsP255 => (_ulong3 | _ulong1 | _ulong2) == 0 && (_ulong0 == 0x0000000000000001);
    public bool IsOneOrZero => (_ulong1 | _ulong2 | _ulong3) == 0 && ((_ulong0 << 1) == 0);
    public bool IsShort => (_ulong1 | _ulong2 | _ulong3) == 0 && ((_ulong0 << 16) == 0);

    public void ToZero()
    {
        _ulong0 = 0;
        _ulong1 = 0;
        _ulong2 = 0;
        _ulong3 = 0;
    }

    public unsafe ZeroPaddedSpan ZeroPaddedSpan
    {
        set
        {
            ReadOnlySpan<byte> span = value.Span;
            ref byte bytes = ref _buffer[0];

            if (span.Length != Size)
            {
                // Not full entry, clear first
                Unsafe.As<byte, Word>(ref bytes) = default;
                span.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, Size - value.Length), value.Length));
            }
            else
            {
                Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(span));
            }
        }
    }

    public unsafe byte[] Array
    {
        get => MemoryMarshal.Cast<Word, byte>(MemoryMarshal.CreateSpan(ref this, 1)).ToArray();
        set
        {
            ref byte bytes = ref _buffer[0];
            if (value.Length != Size)
            {
                // Not full entry, clear first
                Unsafe.As<byte, Word>(ref bytes) = default;
                value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, Size - value.Length), value.Length));
            }
            else
            {
                Unsafe.As<byte, Word>(ref bytes) =
                    Unsafe.As<byte, Word>(ref MemoryMarshal.GetArrayDataReference(value));
            }
        }
    }

    public unsafe ReadOnlySpan<byte> ReadOnlySpan
    {
        get => MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref this, 1));
        set
        {
            ref byte bytes = ref _buffer[0];
            if (value.Length != Size)
            {
                // Not full entry, clear first
                Unsafe.As<byte, Word>(ref bytes) = default;
                value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, Size - value.Length), value.Length));
            }
            else
            {
                Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
            }
        }
    }

    public unsafe Span<byte> Span
    {
        get => MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref this, 1));
        set
        {
            ref byte bytes = ref _buffer[0];

            if (value.Length != Size)
            {
                // Not full entry, clear first
                Unsafe.As<byte, Word>(ref bytes) = default;
                value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, Size - value.Length), value.Length));
            }
            else
            {
                Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
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
            fixed (byte* dest = _buffer)
            {
                ref byte bytes = ref Unsafe.AsRef<byte>(dest);
                Unsafe.As<byte, ValueHash256>(ref bytes) = value;
            }
        }
    }

    public Address Address
    {
        get
        {
            byte[] buffer = new byte[Address.Size];
            MemoryMarshal.Cast<Word, byte>(MemoryMarshal.CreateSpan(ref this, 1)).Slice(Size - Address.Size)
                .CopyTo(buffer);
            return new Address(buffer);
        }
        set =>
            // TODO: optimize like the `master` branch does.
            Span = value.Bytes;
    }

    private static Vector256<byte> shuffler = Vector256.Create(
        (byte)
        31, 30, 29, 28, 27, 26, 25, 24,
        23, 22, 21, 20, 19, 18, 17, 16,
        15, 14, 13, 12, 11, 10, 9, 8,
        7, 6, 5, 4, 3, 2, 1, 0);

    public unsafe UInt256 UInt256
    {
        get
        {
            var data = Unsafe.As<byte, Vector256<byte>>(ref _buffer[0]);
            Vector256<byte> convert = Avx2.Shuffle(data, shuffler);
            Vector256<ulong> permute =
                Avx2.Permute4x64(Unsafe.As<Vector256<byte>, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
            return Unsafe.As<Vector256<ulong>, UInt256>(ref permute);
        }
        set
        {
            Vector256<ulong> permute = Unsafe.As<UInt256, Vector256<ulong>>(ref Unsafe.AsRef(in value));
            Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
            Unsafe.WriteUnaligned(ref _buffer[0],
                Avx2.Shuffle(Unsafe.As<Vector256<ulong>, Vector256<byte>>(ref convert), shuffler));
        }
    }

    public uint UInt0
    {
        get => _uInt0;
        set => _uInt0 = value;
    }

    public byte Byte0
    {
        get => _uByte0;
        set => _uByte0 = value;
    }

    public int Int0
    {
        get => _sInt0;
        set => _sInt0 = value;
    }

    public ulong ULong0
    {
        get => _ulong0;
        set => _ulong0 = value;
    }

    public unsafe long LeadingZeros
    {
        get
        {
            // TODO: do the vectorized version with Extract leading bits
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

    public static readonly MethodInfo GetByte0 = typeof(Word).GetProperty(nameof(Byte0))!.GetMethod;
    public static readonly MethodInfo SetByte0 = typeof(Word).GetProperty(nameof(Byte0))!.SetMethod;

    public static readonly MethodInfo GetInt0 = typeof(Word).GetProperty(nameof(Int0))!.GetMethod;
    public static readonly MethodInfo SetInt0 = typeof(Word).GetProperty(nameof(Int0))!.SetMethod;

    public static readonly MethodInfo GetUInt0 = typeof(Word).GetProperty(nameof(UInt0))!.GetMethod;
    public static readonly MethodInfo SetUInt0 = typeof(Word).GetProperty(nameof(UInt0))!.SetMethod;

    public static readonly MethodInfo GetULong0 = typeof(Word).GetProperty(nameof(ULong0))!.GetMethod;
    public static readonly MethodInfo SetULong0 = typeof(Word).GetProperty(nameof(ULong0))!.SetMethod;

    public static readonly MethodInfo SetToZero = typeof(Word).GetMethod(nameof(ToZero))!;

    public static readonly MethodInfo AreEqual = typeof(Word).GetMethod(nameof(CheckIfEqual))!;

    public static readonly MethodInfo GetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.GetMethod;
    public static readonly MethodInfo SetUInt256 = typeof(Word).GetProperty(nameof(UInt256))!.SetMethod;

    public static readonly MethodInfo GetAddress = typeof(Word).GetProperty(nameof(Address))!.GetMethod;
    public static readonly MethodInfo SetAddress = typeof(Word).GetProperty(nameof(Address))!.SetMethod;

    public static readonly MethodInfo GetKeccak = typeof(Word).GetProperty(nameof(Keccak))!.GetMethod;
    public static readonly MethodInfo SetKeccak = typeof(Word).GetProperty(nameof(Keccak))!.SetMethod;

    public static readonly MethodInfo GetArray = typeof(Word).GetProperty(nameof(Array))!.GetMethod;
    public static readonly MethodInfo SetArray = typeof(Word).GetProperty(nameof(Array))!.SetMethod;

    public static readonly MethodInfo GetMutableSpan = typeof(Word).GetProperty(nameof(Span))!.GetMethod;
    public static readonly MethodInfo SetMutableSpan = typeof(Word).GetProperty(nameof(Span))!.SetMethod;
    public static readonly MethodInfo GetReadOnlySpan = typeof(Word).GetProperty(nameof(ReadOnlySpan))!.GetMethod;
    public static readonly MethodInfo SetReadOnlySpan = typeof(Word).GetProperty(nameof(ReadOnlySpan))!.SetMethod;
    public static readonly MethodInfo SetZeroPaddedSpan = typeof(Word).GetProperty(nameof(ZeroPaddedSpan))!.SetMethod;

    public static explicit operator Word(Span<byte> span)
    {
        var result = new Word();
        result.Span = span;
        return result;
    }

    public override string ToString()
    {
        return UInt256.ToString();
    }
}
