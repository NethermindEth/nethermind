// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

using Word = Vector256<byte>;
using HalfWord = Vector128<byte>;

[StructLayout(LayoutKind.Auto)]
public ref partial struct EvmStack
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int ReturnStackSize = 1025;
    public const int WordSize = 32;
    public const int AddressSize = 20;

    public EvmStack(scoped in int head, ITxTracer txTracer, scoped in Span<byte> bytes)
    {
        Head = head;
        _tracer = txTracer;
        _bytes = bytes;
    }

    private readonly ITxTracer _tracer;
    private readonly Span<byte> _bytes;
    public int Head;
    internal ReadOnlySpan<byte> CodeSection;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PushBytesRef()
    {
        // Workhorse method
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref byte headRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), headOffset * WordSize);
        if (newOffset >= MaxStackSize)
        {
            ThrowEvmStackOverflowException();
        }

        Head = (int)newOffset;
        return ref headRef;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Word PushedHead()
        => ref Unsafe.As<byte, Word>(ref PushBytesRef());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Word CreateWordFromUInt64(ulong value)
        => Vector256.Create(0UL, 0UL, 0UL, value).AsByte();

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushBytes<TTracingInst>(scoped ReadOnlySpan<byte> value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        ref byte dst = ref PushBytesRef();
        ref byte src = ref MemoryMarshal.GetReference(value);

        if (value.Length == WordSize)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                Unsafe.As<byte, Word>(ref dst) = Unsafe.As<byte, Word>(ref src);
            }
            else
            {
                Unsafe.As<byte, HalfWord>(ref dst) = Unsafe.ReadUnaligned<HalfWord>(ref src);
                Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) = Unsafe.ReadUnaligned<HalfWord>(ref Unsafe.Add(ref src, 16));
            }
        }
        else
        {
            PushBytesPartial(ref dst, ref src, (uint)value.Length);
        }
    }

    [SkipLocalsInit]
    private static void PushBytesPartial(ref byte dst, ref byte src, nuint length)
    {
        nuint q = length >> 3;
        nuint r = length & 7;

        ulong partial = r == 0 ? 0UL : PackHiU64(ref src, r);

        ref byte p = ref Unsafe.Add(ref src, (int)r);

        Vector128<ulong> lo, hi;

        if (q == 0)
        {
            lo = default;
            hi = Vector128.Create(0UL, partial);
        }
        else if (q == 1)
        {
            lo = default;
            hi = Vector128.Create(partial, Unsafe.ReadUnaligned<ulong>(ref p));
        }
        else if (q == 2)
        {
            lo = Vector128.Create(0UL, partial);
            hi = Unsafe.ReadUnaligned<Vector128<ulong>>(ref p); // 16B load for lanes 2-3
        }
        else
        {
            // q == 3
            lo = Vector128.Create(partial, Unsafe.ReadUnaligned<ulong>(ref p)); // lane0-1
            hi = Unsafe.ReadUnaligned<Vector128<ulong>>(ref Unsafe.Add(ref p, 8)); // lanes 2-3
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.As<byte, Word>(ref dst) = Vector256.Create(lo, hi).AsByte();
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = lo.AsByte();
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) = hi.AsByte();
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackHiU64(ref byte src, nuint r)
    {
        // r is 1..7. Subtract 1 to get 0..6 for contiguous jump table
        return (r - 1) switch
        {
            0 => (ulong)src << 56,
            1 => (ulong)Unsafe.ReadUnaligned<ushort>(ref src) << 48,
            2 => ((ulong)Unsafe.ReadUnaligned<ushort>(ref src) << 40) |
               ((ulong)Unsafe.Add(ref src, 2) << 56),
            3 => (ulong)Unsafe.ReadUnaligned<uint>(ref src) << 32,
            4 => ((ulong)Unsafe.ReadUnaligned<uint>(ref src) << 24) |
               ((ulong)Unsafe.Add(ref src, 4) << 56),
            5 => ((ulong)Unsafe.ReadUnaligned<uint>(ref src) << 16) |
               ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, 4)) << 48),
            _ => ((ulong)Unsafe.ReadUnaligned<uint>(ref src) << 8) |
               ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, 4)) << 40) |
               ((ulong)Unsafe.Add(ref src, 6) << 56),
        };
    }

    public void PushBytes<TTracingInst>(scoped in ZeroPaddedSpan value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length != WordSize)
        {
            ref byte bytes = ref PushBytesRef();
            // Not full entry, clear first
            Unsafe.As<byte, Word>(ref bytes) = default;
            valueSpan.CopyTo(MemoryMarshal.CreateSpan(ref bytes, value.Length));
        }
        else
        {
            PushedHead() = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan));
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushRightPaddedBytes<TTracingInst>(ref byte src, uint length)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            ReportStackPush(ref src, length);

        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (length != WordSize)
        {
            return PushBytesPartialZeroPadded(ref dst, ref src, length);
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.As<byte, Word>(ref dst) = Unsafe.As<byte, Word>(ref src);
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = Unsafe.ReadUnaligned<HalfWord>(ref src);
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) =
                Unsafe.ReadUnaligned<HalfWord>(ref Unsafe.Add(ref src, 16));
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private static EvmExceptionType PushBytesPartialZeroPadded(ref byte dst, ref byte src, nuint length)
    {
        nuint q = length >> 3; // full 8-byte chunks: 0..3
        nuint r = length & 7;  // remainder: 0..7

        // The partial bytes (if any) live at src + 8*q
        ref byte tail = ref Unsafe.Add(ref src, (int)(q << 3));
        ulong partial = r == 0 ? 0UL : PackLoU64(ref tail, r);

        Vector128<ulong> lo, hi;

        if (q == 0)
        {
            // length 0..7  -> lane0 partial, rest zero
            lo = Vector128.Create(partial, 0UL);
            hi = default;
        }
        else if (q == 1)
        {
            // length 8..15 -> lane0 full, lane1 partial, rest zero
            lo = Vector128.Create(Unsafe.ReadUnaligned<ulong>(ref src), partial);
            hi = default;
        }
        else if (q == 2)
        {
            // length 16..23 -> lanes0..1 full, lane2 partial, lane3 zero
            lo = Unsafe.ReadUnaligned<Vector128<ulong>>(ref src);
            hi = Vector128.Create(partial, 0UL);
        }
        else
        {
            // q == 3, length 24..31 -> lanes0..2 full, lane3 partial
            lo = Unsafe.ReadUnaligned<Vector128<ulong>>(ref src);
            hi = Vector128.Create(
                Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref src, 16)),
                partial);
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.As<byte, Word>(ref dst) = Vector256.Create(lo, hi).AsByte();
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = lo.AsByte();
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) = hi.AsByte();
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackLoU64(ref byte src, nuint r)
    {
        // r is 1..7. Subtract 1 to get 0..6 for contiguous jump table
        return (r - 1) switch
        {
            0 => (ulong)src,
            1 => Unsafe.ReadUnaligned<ushort>(ref src),
            2 => (ulong)Unsafe.ReadUnaligned<ushort>(ref src) |
               ((ulong)Unsafe.Add(ref src, 2) << 16),
            3 => Unsafe.ReadUnaligned<uint>(ref src),
            4 => (ulong)Unsafe.ReadUnaligned<uint>(ref src) |
               ((ulong)Unsafe.Add(ref src, 4) << 32),
            5 => (ulong)Unsafe.ReadUnaligned<uint>(ref src) |
               ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, 4)) << 32),
            _ => (ulong)Unsafe.ReadUnaligned<uint>(ref src) |
               ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref src, 4)) << 32) |
               ((ulong)Unsafe.Add(ref src, 6) << 48),
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ReportStackPush(ref byte span, uint length)
    {
        ReadOnlySpan<byte> value = MemoryMarshal.CreateReadOnlySpan(ref span, (int)length);
        ZeroPaddedSpan padded = new(value, WordSize - value.Length, PadDirection.Right);
        _tracer.ReportStackPush(padded);
    }

    [GenerateStackPushBytes(1, PadDirection.Left)]
    public partial EvmExceptionType PushByte<TTracingInst>(byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(2, PadDirection.Left)]
    public partial EvmExceptionType Push2Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(3, PadDirection.Left)]
    public partial EvmExceptionType Push3Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(4, PadDirection.Left)]
    public partial EvmExceptionType Push4Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(5, PadDirection.Left)]
    public partial EvmExceptionType Push5Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(6, PadDirection.Left)]
    public partial EvmExceptionType Push6Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(7, PadDirection.Left)]
    public partial EvmExceptionType Push7Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(8, PadDirection.Left)]
    public partial EvmExceptionType Push8Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(9, PadDirection.Left)]
    public partial EvmExceptionType Push9Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(10, PadDirection.Left)]
    public partial EvmExceptionType Push10Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(11, PadDirection.Left)]
    public partial EvmExceptionType Push11Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(12, PadDirection.Left)]
    public partial EvmExceptionType Push12Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(13, PadDirection.Left)]
    public partial EvmExceptionType Push13Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(14, PadDirection.Left)]
    public partial EvmExceptionType Push14Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(15, PadDirection.Left)]
    public partial EvmExceptionType Push15Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(16, PadDirection.Left)]
    public partial EvmExceptionType Push16Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(17, PadDirection.Left)]
    public partial EvmExceptionType Push17Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(18, PadDirection.Left)]
    public partial EvmExceptionType Push18Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(19, PadDirection.Left)]
    public partial EvmExceptionType Push19Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(20, PadDirection.Left)]
    public partial EvmExceptionType Push20Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(21, PadDirection.Left)]
    public partial EvmExceptionType Push21Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(22, PadDirection.Left)]
    public partial EvmExceptionType Push22Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(23, PadDirection.Left)]
    public partial EvmExceptionType Push23Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(24, PadDirection.Left)]
    public partial EvmExceptionType Push24Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(25, PadDirection.Left)]
    public partial EvmExceptionType Push25Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(26, PadDirection.Left)]
    public partial EvmExceptionType Push26Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(27, PadDirection.Left)]
    public partial EvmExceptionType Push27Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(28, PadDirection.Left)]
    public partial EvmExceptionType Push28Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(29, PadDirection.Left)]
    public partial EvmExceptionType Push29Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(30, PadDirection.Left)]
    public partial EvmExceptionType Push30Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(31, PadDirection.Left)]
    public partial EvmExceptionType Push31Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [GenerateStackPushBytes(32, PadDirection.Left)]
    public partial EvmExceptionType Push32Bytes<TTracingInst>(ref byte value) where TTracingInst : struct, IFlag;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushAddress<TTracingInst>(Address address)
        where TTracingInst : struct, IFlag
        => Push20Bytes<TTracingInst>(ref MemoryMarshal.GetArrayDataReference(address.Bytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push32Bytes<TTracingInst>(in ValueHash256 hash)
        where TTracingInst : struct, IFlag
        => Push32Bytes<TTracingInst>(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in hash)));

    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType PushBothPaddedBytes<TTracingInst>(ref byte start, int used, int paddingLength)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            ReportStackPush(ref start, used);

        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref byte dst = ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        // Zeros on both sides.
        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.As<byte, Word>(ref dst) = default;
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = default;
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) = default;
        }

        if (paddingLength == WordSize)
        {
            // All padding, nothing to copy
            return EvmExceptionType.None;
        }

        dst = ref Unsafe.Add(ref dst, WordSize - paddingLength);
        CopyUpTo32(ref dst, ref start, (uint)used);
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ReportStackPush(ref byte start, int used)
    {
        _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref start, used));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void CopyUpTo32(ref byte dest, ref byte source, uint len)
    {
        // Take local copy to not get weird with refs
        ref byte dst = ref dest;
        ref byte src = ref source;

        if (len >= 16)
        {
            Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<HalfWord>(ref src));
            len -= 16;
            dst = ref Unsafe.Add(ref dst, 16);
            src = ref Unsafe.Add(ref src, 16);
        }

        if (len >= 8)
        {
            Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<ulong>(ref src));
            len -= 8;
            dst = ref Unsafe.Add(ref dst, 8);
            src = ref Unsafe.Add(ref src, 8);
        }

        if (len >= 4)
        {
            Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<uint>(ref src));
            len -= 4;
            dst = ref Unsafe.Add(ref dst, 4);
            src = ref Unsafe.Add(ref src, 4);
        }

        if (len >= 2)
        {
            Unsafe.WriteUnaligned(ref dst, Unsafe.ReadUnaligned<ushort>(ref src));
            len -= 2;
            dst = ref Unsafe.Add(ref dst, 2);
            src = ref Unsafe.Add(ref src, 2);
        }

        if (len != 0)
        {
            dst = src;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushOne<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.OneByteSpan);

        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize)));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        // Build a 256-bit vector: [ 0, 0, 0, (1UL << 56) ]
        // - when viewed as bytes: all zeros except byte[31] == 1
        if (Vector256.IsHardwareAccelerated)
        {
            // Single 32-byte store
            head = CreateWordFromUInt64(1UL << 56);
        }
        else
        {
            ref HalfWord head128 = ref Unsafe.As<Word, HalfWord>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = Vector128.Create(0UL, 1UL << 56).AsByte();
        }
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushZero<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.ZeroByteSpan);

        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize)));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (Vector256.IsHardwareAccelerated)
        {
            // Single 32-byte store
            head = default;
        }
        else
        {
            ref Vector128<uint> head128 = ref Unsafe.As<Vector256<byte>, Vector128<uint>>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = default;
        }
        return EvmExceptionType.None;
    }

    public EvmExceptionType PushUInt32<TTracingInst>(uint value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize)));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        // uint size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in Unsafe.As<uint, byte>(ref value), sizeof(uint));

        if (Vector256.IsHardwareAccelerated)
        {
            // Single 32-byte store
            head = Vector256.Create(0U, 0U, 0U, 0U, 0U, 0U, 0U, value).AsByte();
        }
        else
        {
            ref Vector128<uint> head128 = ref Unsafe.As<Word, Vector128<uint>>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = Vector128.Create(0U, 0U, 0U, value);
        }
        return EvmExceptionType.None;
    }

    public EvmExceptionType PushUInt64<TTracingInst>(ulong value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize)));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        // ulong size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in Unsafe.As<ulong, byte>(ref value), sizeof(ulong));

        if (Vector256.IsHardwareAccelerated)
        {
            // Single 32-byte store
            head = CreateWordFromUInt64(value);
        }
        else
        {
            ref Vector128<ulong> head128 = ref Unsafe.As<Word, Vector128<ulong>>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = Vector128.Create(0UL, value);
        }
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Pushes an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method is a counterpart to <see cref="PopUInt256"/> and uses the same, raw data approach to write data back.
    /// </remarks>
    [SkipLocalsInit]
    public EvmExceptionType PushUInt256<TTracingInst>(in UInt256 value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), (nint)(headOffset * WordSize)));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (Avx2.IsSupported)
        {
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word data = Unsafe.As<UInt256, Word>(ref Unsafe.AsRef(in value));
                head = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
            }
            else
            {
                Vector256<ulong> permute = Unsafe.As<UInt256, Vector256<ulong>>(ref Unsafe.AsRef(in value));
                Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
                head = Avx2.Shuffle(Unsafe.As<Vector256<ulong>, Word>(ref convert), shuffle);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(value.u3);
                u2 = BinaryPrimitives.ReverseEndianness(value.u2);
                u1 = BinaryPrimitives.ReverseEndianness(value.u1);
                u0 = BinaryPrimitives.ReverseEndianness(value.u0);
            }
            else
            {
                u3 = value.u3;
                u2 = value.u2;
                u1 = value.u1;
                u0 = value.u0;
            }

            head = Vector256.Create(u3, u2, u1, u0).AsByte();
        }

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Word, byte>(ref head), WordSize));

        return EvmExceptionType.None;
    }

    public EvmExceptionType PushSignedInt256<TTracingInst>(in Int256.Int256 value)
        where TTracingInst : struct, IFlag
    {
        // tail call into UInt256
        return PushUInt256<TTracingInst>(in Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopLimbo()
    {
        int head = Head - 1;
        if (head < 0)
        {
            return false;
        }
        Head = head;
        return true;
    }

    /// <summary>
    /// Pops an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method does its own calculations to create the <paramref name="result"/>. It knows that 32 bytes were popped with <see cref="PopBytesByRef"/>. It doesn't have to check the size of span or slice it.
    /// All it does is <see cref="Unsafe.ReadUnaligned{T}(ref byte)"/> and then reverse endianness if needed. Then it creates <paramref name="result"/>.
    /// </remarks>
    /// <param name="result">The returned value.</param>
    [SkipLocalsInit]
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte baseRef = ref MemoryMarshal.GetReference(_bytes);
        int head = Head - 1;
        if (head < 0)
        {
            return false;
        }
        Head = head;
        ref byte bytes = ref Unsafe.Add(ref baseRef, (nint)((uint)head * WordSize));

        if (Avx2.IsSupported)
        {
            Word data = Unsafe.ReadUnaligned<Word>(ref bytes);
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word convert = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
                result = Unsafe.As<Word, UInt256>(ref convert);
            }
            else
            {
                Word convert = Avx2.Shuffle(data, shuffle);
                Vector256<ulong> permute = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
                result = Unsafe.As<Vector256<ulong>, UInt256>(ref permute);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                // Combine read and switch endianness to movbe reg, mem
                u3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref bytes));
                u2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong))));
                u1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong))));
                u0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong))));
            }
            else
            {
                u3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
                u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong)));
                u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong)));
                u0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong)));
            }

            result = new UInt256(u0, u1, u2, u3);
        }

        return true;
    }

    public readonly bool PeekUInt256IsZero()
    {
        ref byte baseRef = ref MemoryMarshal.GetReference(_bytes);
        int head = Head - 1;
        if (head < 0)
        {
            return false;
        }

        return Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref baseRef, (nint)((uint)head * WordSize))) == default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref byte PeekBytesByRef()
    {
        ref byte baseRef = ref MemoryMarshal.GetReference(_bytes);
        int head = Head - 1;
        if (head < 0)
        {
            return ref Unsafe.NullRef<byte>();
        }
        return ref Unsafe.Add(ref baseRef, (nint)((uint)head * WordSize));
    }

    public readonly Span<byte> PeekWord256()
    {
        int head = Head;
        if (head-- == 0)
        {
            ThrowEvmStackUnderflowException();
        }

        return _bytes.Slice(head * WordSize, WordSize);
    }

    public Address? PopAddress() => Head-- != 0 ? new Address(_bytes.Slice(Head * WordSize + WordSize - AddressSize, AddressSize).ToArray()) : null;

    public bool PopAddress(out Address address)
    {
        if (Head-- == 0)
        {
            address = null;
            return false;
        }

        address = new Address(_bytes.Slice(Head * WordSize + WordSize - AddressSize, AddressSize).ToArray());
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PopBytesByRef()
    {
        ref byte baseRef = ref MemoryMarshal.GetReference(_bytes);
        uint head = (uint)Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }
        Head = (int)--head;
        return ref Unsafe.Add(ref baseRef, (nint)(head * WordSize));
    }

    public Span<byte> PopWord256()
    {
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

        return MemoryMarshal.CreateSpan(ref bytes, WordSize);
    }

    public bool PopWord256(out Span<byte> word)
    {
        if (Head-- == 0)
        {
            word = default;
            return false;
        }

        word = _bytes.Slice(Head * WordSize, WordSize);
        return true;
    }

    public byte PopByte()
    {
        ref byte bytes = ref PopBytesByRef();

        if (Unsafe.IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

        return Unsafe.Add(ref bytes, WordSize - sizeof(byte));
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Dup<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        int head = Head;
        if (head < depth)
        {
            return EvmExceptionType.StackUnderflow;
        }

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);
        // Use nuint to eliminate sign extension; parallel shifts
        nuint headOffset = (nuint)(uint)head << 5;
        nuint depthBytes = (nuint)(uint)depth << 5;

        ref byte to = ref Unsafe.Add(ref bytes, headOffset);
        ref byte from = ref Unsafe.Add(ref bytes, headOffset - depthBytes);

        if (TTracingInst.IsActive) Trace(depth);

        if (++head >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }

        Head = head;
        Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));
        return EvmExceptionType.None;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly EvmExceptionType Swap<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        int head = Head;
        if (head < depth)
        {
            return EvmExceptionType.StackUnderflow;
        }

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        nuint headOffset = (nuint)(uint)head << 5;
        nuint depthBytes = (nuint)(uint)depth << 5;

        ref byte bottom = ref Unsafe.Add(ref bytes, headOffset - depthBytes);
        ref byte top = ref Unsafe.Add(ref bytes, headOffset - WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<Word>(ref top));
        Unsafe.WriteUnaligned(ref top, buffer);

        if (TTracingInst.IsActive) Trace(depth);

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Exchange<TTracingInst>(int n, int m)
        where TTracingInst : struct, IFlag
    {
        int maxDepth = Math.Max(n, m);
        if (!EnsureDepth(maxDepth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte first = ref Unsafe.Add(ref bytes, (Head - n) * WordSize);
        ref byte second = ref Unsafe.Add(ref bytes, (Head - m) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref first);
        Unsafe.WriteUnaligned(ref first, Unsafe.ReadUnaligned<Word>(ref second));
        Unsafe.WriteUnaligned(ref second, buffer);

        if (TTracingInst.IsActive) Trace(maxDepth);

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void Trace(int depth)
    {
        for (int i = depth; i > 0; i--)
        {
            _tracer.ReportStackPush(_bytes.Slice(Head * WordSize - i * WordSize, WordSize));
        }
    }

    [StackTraceHidden]
    [DoesNotReturn]
    internal static void ThrowEvmStackUnderflowException()
    {
        Metrics.EvmExceptions++;
        throw new EvmStackUnderflowException();
    }

    [StackTraceHidden]
    [DoesNotReturn]
    internal static void ThrowEvmStackOverflowException()
    {
        Metrics.EvmExceptions++;
        throw new EvmStackOverflowException();
    }
}
