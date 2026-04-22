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
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

using Word = Vector256<byte>;
using HalfWord = Vector128<byte>;

[StructLayout(LayoutKind.Auto)]
public ref struct EvmStack
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int WordSize = 32;
    public const int AddressSize = 20;

    public EvmStack(int head, ITxTracer txTracer, ref byte stack, scoped in ReadOnlySpan<byte> codeSpan)
    {
        Head = head;
        _tracer = txTracer;
        _stack = ref stack;
        Code = ref MemoryMarshal.GetReference(codeSpan);
        CodeLength = codeSpan.Length;
    }

    public EvmStack(int head, ref byte stack, scoped in ReadOnlySpan<byte> codeSpan)
    {
        Head = head;
        _tracer = null;
        _stack = ref stack;
        Code = ref MemoryMarshal.GetReference(codeSpan);
        CodeLength = codeSpan.Length;
    }

    private readonly ITxTracer _tracer;
    private readonly ref byte _stack;
    internal readonly ref byte Code;
    public int Head;
    internal readonly int CodeLength;

    /// <summary>
    /// Reserves the next stack slot and returns a ref to it. On overflow returns <see cref="Unsafe.NullRef{T}"/>;
    /// callers must check with <see cref="Unsafe.IsNullRef{T}"/> before writing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PushBytesRef()
    {
        // Workhorse method
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return ref Unsafe.NullRef<byte>();
        }

        Head = (int)newOffset;
        return ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Word CreateWordFromUInt64(ulong value)
        => Vector256.Create(0UL, 0UL, 0UL, value).AsByte();

    // PSHUFB/PermuteVar32x8 mask that byte-reverses a 256-bit word (big-endian <-> little-endian).
    // Declared as a property so the JIT folds it to a PC-relative rodata load at every call site.
    private static Word ByteSwap256Mask
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            return Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
        }
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushBytes<TTracingInst>(scoped ReadOnlySpan<byte> value)
        where TTracingInst : struct, IFlag
    {
        ref byte dst = ref PushBytesRef();
        if (Unsafe.IsNullRef(ref dst)) return EvmExceptionType.StackOverflow;

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

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

        return EvmExceptionType.None;
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
    => (r - 1) switch
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

    public EvmExceptionType PushBytes<TTracingInst>(scoped in ZeroPaddedSpan value)
        where TTracingInst : struct, IFlag
    {
        ref byte bytes = ref PushBytesRef();
        if (Unsafe.IsNullRef(ref bytes)) return EvmExceptionType.StackOverflow;

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length >= WordSize)
        {
            Debug.Assert(value.Length == WordSize, "Trying to push more than 32 bytes to the stack.");
            Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = default; // Not full entry, clear first
            valueSpan.CopyTo(MemoryMarshal.CreateSpan(ref bytes, value.Length));
        }

        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushRightPaddedBytes<TTracingInst>(ref byte src, uint length)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
            ReportStackPush(ref src, length);

        ref byte dst = ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize));

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

    // r is 1..7. Subtract 1 to get 0..6 for contiguous jump table
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackLoU64(ref byte src, nuint r)
    => (r - 1) switch
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

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ReportStackPush(ref byte span, uint length)
    {
        ReadOnlySpan<byte> value = MemoryMarshal.CreateReadOnlySpan(ref span, (int)length);
        ZeroPaddedSpan padded = new(value, WordSize - value.Length, PadDirection.Right);
        _tracer.ReportStackPush(padded);
    }

    /// <summary>
    /// Reports a UInt256 value at the given stack slot to the tracer (for tracing without push).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void ReportPushUInt256(ref byte slot) =>
        _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref slot, WordSize));

    /// <summary>
    /// Reads a UInt256 value from a stack slot with big-endian to native conversion (no bounds check).
    /// Used when the slot was already validated by a previous operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt256 ReadUInt256FromSlot(ref byte slot)
    {
        if (Avx2.IsSupported)
        {
            Word data = Unsafe.ReadUnaligned<Word>(ref slot);
            Word shuffle = ByteSwap256Mask;
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word convert = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
                return Unsafe.As<Word, UInt256>(ref convert);
            }
            else
            {
                Word convert = Avx2.Shuffle(data, shuffle);
                Vector256<ulong> permute = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
                return Unsafe.As<Vector256<ulong>, UInt256>(ref permute);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref slot));
                u2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, sizeof(ulong))));
                u1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 2 * sizeof(ulong))));
                u0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 3 * sizeof(ulong))));
            }
            else
            {
                u3 = Unsafe.ReadUnaligned<ulong>(ref slot);
                u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, sizeof(ulong)));
                u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 2 * sizeof(ulong)));
                u0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 3 * sizeof(ulong)));
            }
            return new UInt256(u0, u1, u2, u3);
        }
    }

    /// <summary>
    /// Out-parameter form of <see cref="ReadUInt256FromSlot(ref byte)"/>. Writes directly
    /// into <paramref name="value"/>, bypassing the 32-byte return-value staging buffer
    /// the JIT otherwise emits for a by-value UInt256 return.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadUInt256FromSlot(ref byte slot, out UInt256 value)
    {
        Unsafe.SkipInit(out value);
        if (Avx2.IsSupported)
        {
            Word data = Unsafe.ReadUnaligned<Word>(ref slot);
            Word shuffle = ByteSwap256Mask;
            if (Avx512Vbmi.VL.IsSupported)
            {
                Unsafe.As<UInt256, Word>(ref value) = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
            }
            else
            {
                Word convert = Avx2.Shuffle(data, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref value)
                    = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref slot));
                u2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, sizeof(ulong))));
                u1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 2 * sizeof(ulong))));
                u0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 3 * sizeof(ulong))));
            }
            else
            {
                u3 = Unsafe.ReadUnaligned<ulong>(ref slot);
                u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, sizeof(ulong)));
                u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 2 * sizeof(ulong)));
                u0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref slot, 3 * sizeof(ulong)));
            }
            value = new UInt256(u0, u1, u2, u3);
        }
    }

    /// <summary>
    /// Writes a UInt256 value to a stack slot with big-endian conversion (no bounds check).
    /// Used when the slot was already validated by a previous pop operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt256ToSlot(ref byte slot, in UInt256 value)
    {
        ref Word head = ref Unsafe.As<byte, Word>(ref slot);
        if (Avx2.IsSupported)
        {
            Word shuffle = ByteSwap256Mask;
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
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push10Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 10);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        // This avoids expensive vpinsrq + vinserti128 dependency chain.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = (ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 48;
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 2));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push11Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 11);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        // This avoids expensive vpinsrq + vinserti128 dependency chain.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = ((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 40) | ((ulong)Unsafe.Add(ref value, 2) << 56);
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 3));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push12Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 12);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = (ulong)Unsafe.ReadUnaligned<uint>(ref value) << 32;
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 4));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push13Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 13);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 24) | ((ulong)Unsafe.Add(ref value, 4) << 56);
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 5));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push14Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 14);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 16) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 48);
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 6));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push15Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 15);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 8) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 40) | ((ulong)Unsafe.Add(ref value, 6) << 56);
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 7));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push16Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 16);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));
        HalfWord src = Unsafe.ReadUnaligned<HalfWord>(ref value);

        if (Vector256.IsHardwareAccelerated)
        {
            head = Vector256.Create(default, src);
        }
        else
        {
            ref HalfWord head128 = ref Unsafe.As<Word, HalfWord>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = src;
        }

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push17Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 17);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = ((ulong)Unsafe.Add(ref value, 0)) << 56;
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 1));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 9));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push18Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 18);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = (ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 48;
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 2));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 10));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push19Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 19);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = ((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 40) | ((ulong)Unsafe.Add(ref value, 2) << 56);
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 3));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 11));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push20Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 20);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = (ulong)Unsafe.ReadUnaligned<uint>(ref value) << 32;
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 4));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 12));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push21Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 21);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 24) | ((ulong)Unsafe.Add(ref value, 4) << 56);
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 5));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 13));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push22Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 22);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 16) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 48);
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 6));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 14));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push23Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 23);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 8) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 40) | ((ulong)Unsafe.Add(ref value, 6) << 56);
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 7));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 15));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push24Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 24);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref value);
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 8));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 16));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push25Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 25);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = ((ulong)Unsafe.Add(ref value, 0)) << 56;
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 1));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 9));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 17));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push26Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 26);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = (ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 48;
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 2));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 10));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 18));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push27Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 27);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = ((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 40) | ((ulong)Unsafe.Add(ref value, 2) << 56);
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 3));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 11));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 19));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push28Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 28);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = (ulong)Unsafe.ReadUnaligned<uint>(ref value) << 32;
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 4));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 12));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 20));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push29Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 29);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 24) | ((ulong)Unsafe.Add(ref value, 4) << 56);
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 5));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 13));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 21));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push2Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 2);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Build the full 32-byte value in a register and emit a single vector store;
        // zero-then-overwrite would be two stores.
        head = CreateWordFromUInt64((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 48);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push30Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 30);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 16) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 48);
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 6));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 14));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 22));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push31Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 31);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Write all 4 lanes directly with scalar stores (no zeroing needed).
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        headU64 = ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 8) | ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 40) | ((ulong)Unsafe.Add(ref value, 6) << 56);
        Unsafe.Add(ref headU64, 1) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 7));
        Unsafe.Add(ref headU64, 2) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 15));
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 23));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push32Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 32);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));
        head = Unsafe.ReadUnaligned<Word>(ref value);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push3Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 3);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        head = CreateWordFromUInt64(
            ((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 40) |
            ((ulong)Unsafe.Add(ref value, 2) << 56));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push4Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 4);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        head = CreateWordFromUInt64((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 32);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push5Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 5);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        head = CreateWordFromUInt64(
            ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 24) |
            ((ulong)Unsafe.Add(ref value, 4) << 56));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push6Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 6);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        head = CreateWordFromUInt64(
            ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 16) |
            ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 48));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push7Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 7);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        head = CreateWordFromUInt64(
            ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 8) |
            ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 40) |
            ((ulong)Unsafe.Add(ref value, 6) << 56));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push8Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 8);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        head = CreateWordFromUInt64(Unsafe.ReadUnaligned<ulong>(ref value));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push9Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 9);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill non-zero lanes with scalar stores.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 2) = ((ulong)Unsafe.Add(ref value, 0)) << 56;
        Unsafe.Add(ref headU64, 3) = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, 1));

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushByte<TTracingInst>(byte value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(value);
        }

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        // Zero entire word with single vector store, then fill lane 3 with scalar store.
        head = default;
        ref ulong headU64 = ref Unsafe.As<Word, ulong>(ref head);
        Unsafe.Add(ref headU64, 3) = (ulong)value << 56;

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushAddress<TTracingInst>(Address address)
        where TTracingInst : struct, IFlag
        => Push20Bytes<TTracingInst>(ref MemoryMarshal.GetReference(address.Bytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push32Bytes<TTracingInst>(in ValueHash256 hash)
        where TTracingInst : struct, IFlag
        => Push32Bytes<TTracingInst>(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in hash)));

    /// <summary>
    /// Fallback writer for truncated PUSH{n} where fewer than <paramref name="pushSize"/> immediate
    /// bytes remain in code. Zero-fills the 32-byte word, then copies <paramref name="used"/> bytes
    /// to the leading portion of the n-byte PUSH slot (high end in big-endian layout).
    /// </summary>
    /// <param name="start">Reference to the first immediate byte in code.</param>
    /// <param name="used">Number of immediate bytes available in code (0 <= used <= pushSize).</param>
    /// <param name="pushSize">The PUSH opcode's declared immediate length (2..32).</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType PushBothPaddedBytes<TTracingInst>(ref byte start, int used, int pushSize)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
            ReportStackPush(ref start, used);

        ref byte dst = ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize));

        // Truncated PUSH32 is just a right-padded partial write, so reuse the tighter helper.
        if (pushSize == WordSize)
        {
            return PushBytesPartialZeroPadded(ref dst, ref start, (uint)used);
        }

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

        // When no immediate bytes are available (truncated PUSH at end of code), the
        // zero-filled word is already correct.
        if (used == 0)
        {
            return EvmExceptionType.None;
        }

        // Copy `used` bytes to the high end of the `pushSize`-byte tail. Positions
        // [WordSize - pushSize + used, WordSize) stay zero as the spec requires.
        dst = ref Unsafe.Add(ref dst, WordSize - pushSize);
        CopyUpTo32(ref dst, ref start, (uint)used);
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void ReportStackPush(ref byte start, int used)
        => _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref start, used));

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
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.OneByteSpan);

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

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
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.ZeroByteSpan);

        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));

        if (Vector256.IsHardwareAccelerated)
        {
            // Single 32-byte store
            head = default;
        }
        else
        {
            ref Vector128<uint> head128 = ref Unsafe.As<Word, Vector128<uint>>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = default;
        }
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushUInt32<TTracingInst>(uint value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushUInt64<TTracingInst>(ulong value)
        where TTracingInst : struct, IFlag
    {
        uint headOffset = (uint)Head;
        uint newOffset = headOffset + 1;
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));
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
        ref Word head = ref Unsafe.As<byte, Word>(ref Unsafe.Add(ref _stack, (nint)(headOffset * WordSize)));
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = (int)newOffset;

        if (Avx2.IsSupported)
        {
            Word shuffle = ByteSwap256Mask;
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
        => PushUInt256<TTracingInst>(in Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));

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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte baseRef = ref _stack;
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
            Word shuffle = ByteSwap256Mask;
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

    /// <summary>
    /// Pops two UInt256 values written in big endian, amortising bounds checking
    /// and offset calculation costs.
    /// </summary>
    /// <param name="a">First popped value (was at top of stack).</param>
    /// <param name="b">Second popped value (was deeper).</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256(out UInt256 a, out UInt256 b)
    {
        Unsafe.SkipInit(out a);
        Unsafe.SkipInit(out b);

        int head = Head;
        int newHead = head - 2;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;

        ref byte bytes = ref Unsafe.Add(ref _stack, (nint)((uint)newHead * WordSize));
        // Memory layout: [b @ +0] [a @ +32]

        if (Avx2.IsSupported)
        {
            Word shuffle = ByteSwap256Mask;

            // Process each value completely before starting the next to reduce register pressure.
            // Write directly to the out parameters to avoid intermediate local variable copies.
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word bData = Unsafe.ReadUnaligned<Word>(ref bytes);
                Unsafe.As<UInt256, Word>(ref b) = Avx512Vbmi.VL.PermuteVar32x8(bData, shuffle);

                Word aData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 32));
                Unsafe.As<UInt256, Word>(ref a) = Avx512Vbmi.VL.PermuteVar32x8(aData, shuffle);
            }
            else
            {
                const byte SwapHalves = 0b_01_00_11_10;

                Word bData = Unsafe.ReadUnaligned<Word>(ref bytes);
                Word bShuf = Avx2.Shuffle(bData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref b) = Avx2.Permute4x64(bShuf.AsUInt64(), SwapHalves);

                Word aData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 32));
                Word aShuf = Avx2.Shuffle(aData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref a) = Avx2.Permute4x64(aShuf.AsUInt64(), SwapHalves);
            }
        }
        else
        {
            // Scalar path - interleave loads across both values
            if (BitConverter.IsLittleEndian)
            {
                ulong b3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref bytes));
                ulong a3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32)));

                ulong b2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8)));
                ulong a2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40)));

                ulong b1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16)));
                ulong a1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48)));

                ulong b0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24)));
                ulong a0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56)));

                b = new UInt256(b0, b1, b2, b3);
                a = new UInt256(a0, a1, a2, a3);
            }
            else
            {
                ulong b3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
                ulong a3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32));

                ulong b2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8));
                ulong a2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40));

                ulong b1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16));
                ulong a1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48));

                ulong b0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24));
                ulong a0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56));

                b = new UInt256(b0, b1, b2, b3);
                a = new UInt256(a0, a1, a2, a3);
            }
        }

        return true;
    }

    /// <summary>
    /// Pops three UInt256 values written in big endian, amortising bounds checking
    /// and offset calculation costs.
    /// </summary>
    /// <param name="a">First popped value (was at top of stack).</param>
    /// <param name="b">Second popped value.</param>
    /// <param name="c">Third popped value (was deepest).</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256(out UInt256 a, out UInt256 b, out UInt256 c)
    {
        Unsafe.SkipInit(out a);
        Unsafe.SkipInit(out b);
        Unsafe.SkipInit(out c);

        int head = Head;
        int newHead = head - 3;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;

        ref byte bytes = ref Unsafe.Add(ref _stack, (nint)((uint)newHead * WordSize));
        // Memory layout: [c @ +0] [b @ +32] [a @ +64]

        if (Avx2.IsSupported)
        {
            // Hoist shuffle mask - same for all three operations
            Word shuffle = ByteSwap256Mask;

            // Process each value completely before starting the next to reduce register pressure.
            // Write directly to the out parameters to avoid intermediate local variable copies.
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word cData = Unsafe.ReadUnaligned<Word>(ref bytes);
                Unsafe.As<UInt256, Word>(ref c) = Avx512Vbmi.VL.PermuteVar32x8(cData, shuffle);

                Word bData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 32));
                Unsafe.As<UInt256, Word>(ref b) = Avx512Vbmi.VL.PermuteVar32x8(bData, shuffle);

                Word aData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 64));
                Unsafe.As<UInt256, Word>(ref a) = Avx512Vbmi.VL.PermuteVar32x8(aData, shuffle);
            }
            else
            {
                const byte SwapHalves = 0b_01_00_11_10;

                Word cData = Unsafe.ReadUnaligned<Word>(ref bytes);
                Word cShuf = Avx2.Shuffle(cData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref c) = Avx2.Permute4x64(cShuf.AsUInt64(), SwapHalves);

                Word bData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 32));
                Word bShuf = Avx2.Shuffle(bData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref b) = Avx2.Permute4x64(bShuf.AsUInt64(), SwapHalves);

                Word aData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 64));
                Word aShuf = Avx2.Shuffle(aData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref a) = Avx2.Permute4x64(aShuf.AsUInt64(), SwapHalves);
            }
        }
        else
        {
            // Scalar path - interleave loads across all three values
            // to break dependency chains and hide load-to-use latency.
            // Modern CPUs can have 10+ loads in flight simultaneously.
            if (BitConverter.IsLittleEndian)
            {
                // Round 1: high qwords (u3) from each value
                ulong c3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref bytes));
                ulong b3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32)));
                ulong a3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 64)));

                // Round 2: u2 from each value
                ulong c2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8)));
                ulong b2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40)));
                ulong a2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 72)));

                // Round 3: u1 from each value
                ulong c1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16)));
                ulong b1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48)));
                ulong a1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 80)));

                // Round 4: low qwords (u0) from each value
                ulong c0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24)));
                ulong b0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56)));
                ulong a0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 88)));

                c = new UInt256(c0, c1, c2, c3);
                b = new UInt256(b0, b1, b2, b3);
                a = new UInt256(a0, a1, a2, a3);
            }
            else
            {
                ulong c3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
                ulong b3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32));
                ulong a3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 64));

                ulong c2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8));
                ulong b2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40));
                ulong a2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 72));

                ulong c1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16));
                ulong b1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48));
                ulong a1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 80));

                ulong c0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24));
                ulong b0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56));
                ulong a0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 88));

                c = new UInt256(c0, c1, c2, c3);
                b = new UInt256(b0, b1, b2, b3);
                a = new UInt256(a0, a1, a2, a3);
            }
        }

        return true;
    }

    /// <summary>
    /// Pops four UInt256 values written in big endian, amortising bounds checking
    /// and offset calculation costs.
    /// </summary>
    /// <param name="a">First popped value (was at top of stack).</param>
    /// <param name="b">Second popped value.</param>
    /// <param name="c">Third popped value.</param>
    /// <param name="d">Fourth popped value (was deepest).</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256(out UInt256 a, out UInt256 b, out UInt256 c, out UInt256 d)
    {
        Unsafe.SkipInit(out a);
        Unsafe.SkipInit(out b);
        Unsafe.SkipInit(out c);
        Unsafe.SkipInit(out d);

        int head = Head;
        int newHead = head - 4;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;

        ref byte bytes = ref Unsafe.Add(ref _stack, (nint)((uint)newHead * WordSize));
        // Memory layout: [d @ +0] [c @ +32] [b @ +64] [a @ +96]

        if (Avx2.IsSupported)
        {
            Word shuffle = ByteSwap256Mask;

            // Process each value completely before starting the next to reduce register pressure.
            // Write directly to the out parameters to avoid intermediate local variable copies.
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word dData = Unsafe.ReadUnaligned<Word>(ref bytes);
                Unsafe.As<UInt256, Word>(ref d) = Avx512Vbmi.VL.PermuteVar32x8(dData, shuffle);

                Word cData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 32));
                Unsafe.As<UInt256, Word>(ref c) = Avx512Vbmi.VL.PermuteVar32x8(cData, shuffle);

                Word bData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 64));
                Unsafe.As<UInt256, Word>(ref b) = Avx512Vbmi.VL.PermuteVar32x8(bData, shuffle);

                Word aData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 96));
                Unsafe.As<UInt256, Word>(ref a) = Avx512Vbmi.VL.PermuteVar32x8(aData, shuffle);
            }
            else
            {
                const byte SwapHalves = 0b_01_00_11_10;

                Word dData = Unsafe.ReadUnaligned<Word>(ref bytes);
                Word dShuf = Avx2.Shuffle(dData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref d) = Avx2.Permute4x64(dShuf.AsUInt64(), SwapHalves);

                Word cData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 32));
                Word cShuf = Avx2.Shuffle(cData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref c) = Avx2.Permute4x64(cShuf.AsUInt64(), SwapHalves);

                Word bData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 64));
                Word bShuf = Avx2.Shuffle(bData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref b) = Avx2.Permute4x64(bShuf.AsUInt64(), SwapHalves);

                Word aData = Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref bytes, 96));
                Word aShuf = Avx2.Shuffle(aData, shuffle);
                Unsafe.As<UInt256, Vector256<ulong>>(ref a) = Avx2.Permute4x64(aShuf.AsUInt64(), SwapHalves);
            }
        }
        else
        {
            // Scalar path - interleave loads across all four values
            // to maximise load unit utilisation and hide latency
            if (BitConverter.IsLittleEndian)
            {
                // Round 1: high qwords (u3)
                ulong d3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref bytes));
                ulong c3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32)));
                ulong b3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 64)));
                ulong a3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 96)));

                // Round 2: u2
                ulong d2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8)));
                ulong c2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40)));
                ulong b2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 72)));
                ulong a2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 104)));

                // Round 3: u1
                ulong d1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16)));
                ulong c1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48)));
                ulong b1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 80)));
                ulong a1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 112)));

                // Round 4: low qwords (u0)
                ulong d0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24)));
                ulong c0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56)));
                ulong b0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 88)));
                ulong a0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 120)));

                d = new UInt256(d0, d1, d2, d3);
                c = new UInt256(c0, c1, c2, c3);
                b = new UInt256(b0, b1, b2, b3);
                a = new UInt256(a0, a1, a2, a3);
            }
            else
            {
                ulong d3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
                ulong c3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 32));
                ulong b3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 64));
                ulong a3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 96));

                ulong d2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 8));
                ulong c2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 40));
                ulong b2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 72));
                ulong a2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 104));

                ulong d1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 16));
                ulong c1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 48));
                ulong b1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 80));
                ulong a1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 112));

                ulong d0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 24));
                ulong c0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 56));
                ulong b0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 88));
                ulong a0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 120));

                d = new UInt256(d0, d1, d2, d3);
                c = new UInt256(c0, c1, c2, c3);
                b = new UInt256(b0, b1, b2, b3);
                a = new UInt256(a0, a1, a2, a3);
            }
        }

        return true;
    }

    public readonly bool PeekUInt256IsZero()
    {
        ref byte baseRef = ref _stack;
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
        ref byte baseRef = ref _stack;
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

        return MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _stack, (nint)((uint)head * WordSize)), WordSize);
    }

    public Address? PopAddress()
    {
        int head = Head - 1;
        if (head < 0) return null;
        Head = head;
        return new Address(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _stack, (nint)((uint)head * WordSize) + WordSize - AddressSize), AddressSize));
    }

    public bool PopAddress(out Address address)
    {
        int head = Head - 1;
        if (head < 0)
        {
            address = null;
            return false;
        }
        Head = head;
        address = new Address(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _stack, (nint)((uint)head * WordSize) + WordSize - AddressSize), AddressSize));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PopBytesByRef()
    {
        ref byte baseRef = ref _stack;
        uint head = (uint)Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }
        Head = (int)--head;
        return ref Unsafe.Add(ref baseRef, (nint)(head * WordSize));
    }

    /// <summary>
    /// Atomic pop-1 + peek-top for binary ops that push exactly one result.
    /// Single bounds check (needs <c>Head &gt;= 2</c>). On success <c>Head</c> decrements by 1
    /// and the returned ref addresses the new top slot so the caller can write the result
    /// in-place without a separate push (which would retest stack overflow).
    /// Caller checks <paramref name="isValid"/> before using the returned ref.
    /// </summary>
    /// <param name="a">The popped value (was at the top of the stack).</param>
    /// <param name="isValid">True on success.</param>
    /// <returns>Reference to the new top slot (32 bytes). Undefined when <paramref name="isValid"/> is false.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    public ref byte Pop1Peek32Bytes(out UInt256 a, out bool isValid)
    {
        Unsafe.SkipInit(out a);
        ref byte baseRef = ref _stack;
        uint head = (uint)Head;
        if (head < 2)
        {
            isValid = false;
            return ref baseRef;
        }
        Head = (int)(head - 1);
        ref byte topRef = ref Unsafe.Add(ref baseRef, (nint)((head - 2) * WordSize));
        ReadUInt256FromSlot(ref Unsafe.Add(ref topRef, WordSize), out a);
        isValid = true;
        return ref topRef;
    }

    /// <summary>
    /// Atomic pop-2 + peek-top for ternary ops that push exactly one result.
    /// Single bounds check (needs <c>Head &gt;= 3</c>). On success <c>Head</c> decrements by 2
    /// and the returned ref addresses the new top slot for in-place write.
    /// Caller checks <paramref name="isValid"/> before using the returned ref.
    /// </summary>
    /// <param name="a">The first popped value (was at the top of the stack).</param>
    /// <param name="b">The second popped value (was below <paramref name="a"/>).</param>
    /// <param name="isValid">True on success.</param>
    /// <returns>Reference to the new top slot (32 bytes). Undefined when <paramref name="isValid"/> is false.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    public ref byte Pop2Peek32Bytes(out UInt256 a, out UInt256 b, out bool isValid)
    {
        Unsafe.SkipInit(out a);
        Unsafe.SkipInit(out b);
        ref byte baseRef = ref _stack;
        uint head = (uint)Head;
        if (head < 3)
        {
            isValid = false;
            return ref baseRef;
        }
        Head = (int)(head - 2);
        ref byte topRef = ref Unsafe.Add(ref baseRef, (nint)((head - 3) * WordSize));
        // Both popped slots sit above the peek slot at +WordSize and +2*WordSize.
        ReadUInt256FromSlot(ref Unsafe.Add(ref topRef, WordSize), out b);
        ReadUInt256FromSlot(ref Unsafe.Add(ref topRef, 2 * WordSize), out a);
        isValid = true;
        return ref topRef;
    }

    /// <summary>
    /// Pops a 32-byte word from the stack. Unlike the other pop operations on this type,
    /// this overload throws <see cref="EvmStackUnderflowException"/> on underflow rather than
    /// signalling via return value.
    /// </summary>
    public Span<byte> PopWord256()
    {
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

        return MemoryMarshal.CreateSpan(ref bytes, WordSize);
    }

    /// <summary>
    /// Atomic pop of a UInt256 offset + a raw 32-byte word with a single bounds check.
    /// Callers such as MSTORE pop both in sequence; amortising avoids a redundant
    /// underflow check and resolves the mismatched throw/try-pattern on the two reads.
    /// </summary>
    /// <param name="a">The top-of-stack value decoded as a big-endian UInt256 (offset for MSTORE).</param>
    /// <param name="word">A span over the second slot, 32 bytes of raw stack-native (big-endian) data.</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256AndWord256(out UInt256 a, out Span<byte> word)
    {
        Unsafe.SkipInit(out a);
        int newHead = Head - 2;
        if (newHead < 0)
        {
            word = default;
            return false;
        }
        Head = newHead;
        ref byte baseRef = ref _stack;
        ReadUInt256FromSlot(ref Unsafe.Add(ref baseRef, (nint)((uint)(newHead + 1) * WordSize)), out a);
        word = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref baseRef, (nint)((uint)newHead * WordSize)), WordSize);
        return true;
    }

    public bool PopWord256(out Span<byte> word)
    {
        int head = Head - 1;
        if (head < 0)
        {
            word = default;
            return false;
        }
        Head = head;
        word = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _stack, (nint)((uint)head * WordSize)), WordSize);
        return true;
    }

    public int PopByte()
    {
        int head = Head;
        if (head == 0) goto Underflow;

        Head = head - 1;
        ref byte slot = ref Unsafe.Add(ref _stack, (head - 1) << 5);

        // Read 8 bytes ending at position 31, extract MSB (byte 31 -> bits 56-63)
        ulong value = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref slot, 24));
        return (byte)(value >> 56);

    Underflow:
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopSmallIndex(out uint value)
    {
        int head = Head;
        if (head == 0)
        {
            value = 0;
            return false;
        }

        Head = head - 1;
        ref byte slot = ref Unsafe.Add(ref _stack, (head - 1) << 5);

        // Check upper 24 bytes are zero (big-endian stack)
        // If any are non-zero, return uint.MaxValue to signal "large value"
        if (Avx2.IsSupported)
        {
            // Load bytes 0-23, check all zero
            HalfWord lower = Unsafe.As<byte, HalfWord>(ref slot);
            ulong upper = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref slot, 16));

            if (!lower.Equals(default) | upper != 0)
            {
                value = uint.MaxValue; // Signals a >= 32
                return true;
            }
        }
        else
        {
            ulong u0 = Unsafe.As<byte, ulong>(ref slot);
            ulong u1 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref slot, 8));
            ulong u2 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref slot, 16));

            if ((u0 | u1 | u2) != 0)
            {
                value = uint.MaxValue;
                return true;
            }
        }

        // Read lower 8 bytes and extract (big-endian, so byte-swap)
        ulong low = BinaryPrimitives.ReverseEndianness(
            Unsafe.As<byte, ulong>(ref Unsafe.Add(ref slot, 24)));

        // If > uint.MaxValue, clamp to signal "large"
        value = low <= uint.MaxValue ? (uint)low : uint.MaxValue;
        return true;
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

        ref byte bytes = ref _stack;
        // Use nuint to eliminate sign extension; parallel shifts
        nuint headOffset = (nuint)(uint)head << 5;
        nuint depthBytes = (nuint)(uint)depth << 5;

        ref byte to = ref Unsafe.Add(ref bytes, headOffset);
        ref byte from = ref Unsafe.Add(ref bytes, headOffset - depthBytes);

        if (++head >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }

        if (TTracingInst.IsActive) Trace(depth);

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

        ref byte bytes = ref _stack;

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
    public readonly EvmExceptionType Exchange<TTracingInst>(int n, int m)
        where TTracingInst : struct, IFlag
    {
        int maxDepth = Math.Max(n, m);
        if (!EnsureDepth(maxDepth)) return EvmExceptionType.StackUnderflow;

        ref byte bytes = ref _stack;

        nuint headOffset = (nuint)(uint)Head * WordSize;
        ref byte first = ref Unsafe.Add(ref bytes, headOffset - (nuint)(uint)n * WordSize);
        ref byte second = ref Unsafe.Add(ref bytes, headOffset - (nuint)(uint)m * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref first);
        Unsafe.WriteUnaligned(ref first, Unsafe.ReadUnaligned<Word>(ref second));
        Unsafe.WriteUnaligned(ref second, buffer);

        if (TTracingInst.IsActive) Trace(maxDepth);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void Trace(int depth)
    {
        for (int i = depth; i > 0; i--)
        {
            _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref _stack, Head * WordSize - i * WordSize), WordSize));
        }
    }

    [StackTraceHidden]
    [DoesNotReturn]
    internal static void ThrowEvmStackUnderflowException()
    {
        Metrics.EvmExceptions++;
        throw new EvmStackUnderflowException();
    }

}
