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
    public const int ReturnStackSize = 1025;
    public const int WordSize = 32;
    public const int AddressSize = 20;

    public ReadOnlySpan<byte> _maskIndices256Bit = new byte[] {
        31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16,
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0
    };

    public ReadOnlySpan<byte> _maskIndices128Bit = new byte[] {
        15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1, 0
    };


    public EvmStack(scoped in int head, ITxTracer txTracer, scoped in Span<byte> bytes)
    {
        Head = head;
        _tracer = txTracer;
        _bytes = bytes;
    }

    private readonly ITxTracer _tracer;
    private readonly Span<byte> _bytes;
    public int Head;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PushBytesRef()
    {
        // Workhorse method
        int head = Head;
        if ((Head = head + 1) >= MaxStackSize)
        {
            ThrowEvmStackOverflowException();
        }

        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ref Word PushedHead()
        => ref Unsafe.As<byte, Word>(ref PushBytesRef());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Word CreateWordFromUInt64(ulong value)
        => Vector256.Create(value, 0UL, 0UL, 0UL).AsByte();

    /// <summary>
    /// Writes a big-endian word (or trimmed big-endian prefix) into the little-endian stack layout.
    /// </summary>
    public void PushBytes<TTracingInst>(scoped ReadOnlySpan<byte> value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(value);
        }

        // Stack storage is little-endian: byte[0] is least significant.
        // Incoming 'value' is big-endian (most significant at index 0).
        ref byte dst = ref PushBytesRef();

        // Clear the whole 32-byte word first.
        Unsafe.As<byte, Word>(ref dst) = default;

        int srcLen = value.Length;
        if (srcLen == 0)
        {
            return;
        }

        // We copy from the end of the big-endian span into the beginning of the word:
        // value[^1] (LSB) -> dst[0], value[^2] -> dst[1], ...
        int count = srcLen > WordSize ? WordSize : srcLen;
        ref byte dstRef = ref dst;
        for (int i = 0; i < count; i++)
        {
            ref byte srcRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(value), srcLen - 1 - i);
            Unsafe.Add(ref dstRef, i) = srcRef;
        }
    }

    /// <summary>
    /// Writes a big-endian, left-padded word coming from a ZeroPaddedSpan into the little-endian stack layout.
    /// The span itself is already zero-padded on the left (big-endian), but we must convert to little-endian.
    /// </summary>
    public void PushBytes<TTracingInst>(scoped in ZeroPaddedSpan value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(value);
        }

        ReadOnlySpan<byte> valueSpan = value.Span;

        ref byte dst = ref PushBytesRef();

        // Clear the whole 32-byte word first.
        Unsafe.As<byte, Word>(ref dst) = default;

        int srcLen = valueSpan.Length;
        if (srcLen == 0)
        {
            return;
        }

        // Same logic as above: valueSpan is big-endian, we store little-endian.
        int count = srcLen > WordSize ? WordSize : srcLen;
        ref byte dstRef = ref dst;
        for (int i = 0; i < count; i++)
        {
            ref byte srcRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(valueSpan), srcLen - 1 - i);
            Unsafe.Add(ref dstRef, i) = srcRef;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushByte<TTracingInst>(byte value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(value);
        }

        // Little-endian stack: put the single byte at offset 0.
        ref Word head = ref PushedHead();
        head = CreateWordFromUInt64(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push2Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // ushort size
        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, sizeof(ushort));
        }

        ushort v = Unsafe.As<byte, ushort>(ref value);
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        ref Word head = ref PushedHead();
        head = CreateWordFromUInt64(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push4Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // uint size
        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, sizeof(uint));
        }

        uint v = Unsafe.As<byte, uint>(ref value);
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        ref Word head = ref PushedHead();
        head = CreateWordFromUInt64(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push8Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // ulong size
        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, sizeof(ulong));
        }

        ulong v = Unsafe.As<byte, ulong>(ref value);
        if (!BitConverter.IsLittleEndian)
        {
            v = BinaryPrimitives.ReverseEndianness(v);
        }

        ref Word head = ref PushedHead();
        head = CreateWordFromUInt64(v);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push16Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // UInt128 size (16 bytes)
        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, sizeof(HalfWord));
        }

        HalfWord halfWord = Unsafe.As<byte, HalfWord>(ref value);

        // invert value from big-endian to little-endian

        Vector128<byte> mask = Vector128.Create(_maskIndices128Bit);

        // Shuffle the vector using the reversal mask
        PushedHead() = Vector256.Create(Vector128<byte>.Zero, Vector128.Shuffle(halfWord, mask));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push20Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // Address size
        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 20);
        }

        // Addresses are passed as big-endian 20-byte sequences.
        // We store them little-endian in the lowest 20 bytes.
        Span<byte> tmp = stackalloc byte[WordSize];
        tmp.Clear();

        ref byte srcRef = ref value;
        for (int i = 0; i < AddressSize; i++)
        {
            tmp[i] = Unsafe.Add(ref srcRef, AddressSize - 1 - i);
        }

        ref Word head = ref PushedHead();
        head = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(tmp));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAddress<TTracingInst>(Address address)
        where TTracingInst : struct, IFlag
        => Push20Bytes<TTracingInst>(ref MemoryMarshal.GetArrayDataReference(address.Bytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes<TTracingInst>(in Word value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.TraceWord(in value);
        }

        // invert value from big-endian to little-endian

        Vector256<byte> mask = Vector256.Create(_maskIndices256Bit);

        // Shuffle the vector using the reversal mask
        PushedHead() = Vector256.Shuffle(value, mask);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes<TTracingInst>(in ValueHash256 hash)
        where TTracingInst : struct, IFlag
        => Push32Bytes<TTracingInst>(in Unsafe.As<ValueHash256, Word>(ref Unsafe.AsRef(in hash)));

    /// <summary>
    /// Pushes big-endian bytes with explicit left padding into a little-endian word.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushLeftPaddedBytes<TTracingInst>(ReadOnlySpan<byte> value, int paddingLength)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(value);
        }

        // Same semantics as PushBytes: incoming big-endian, stack is little-endian.
        ref byte dst = ref PushBytesRef();
        Unsafe.As<byte, Word>(ref dst) = default;

        int srcLen = value.Length;
        if (srcLen == 0)
        {
            return;
        }

        int count = srcLen > WordSize ? WordSize : srcLen;
        ref byte dstRef = ref dst;
        for (int i = 0; i < count; i++)
        {
            ref byte srcRef = ref Unsafe.Add(ref MemoryMarshal.GetReference(value), srcLen - 1 - i);
            Unsafe.Add(ref dstRef, i) = srcRef;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushOne<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(Bytes.OneByteSpan);
        }

        // Little-endian: value 1 is 0x01 at offset 0.
        PushedHead() = CreateWordFromUInt64(1UL);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushZero<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(Bytes.ZeroByteSpan);
        }

        PushedHead() = default;
    }

    public unsafe void PushUInt32<TTracingInst>(uint value)
        where TTracingInst : struct, IFlag
    {
        // Store as native little-endian in the low 4 bytes.
        if (!BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in Unsafe.As<uint, byte>(ref value), sizeof(uint));
        }

        ref Word head = ref PushedHead();
        head = CreateWordFromUInt64(value);
    }

    public unsafe void PushUInt64<TTracingInst>(ulong value)
        where TTracingInst : struct, IFlag
    {
        // Store as native little-endian in the low 8 bytes.
        if (!BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in Unsafe.As<ulong, byte>(ref value), sizeof(ulong));
        }

        PushedHead() = CreateWordFromUInt64(value);
    }

    /// <summary>
    /// Pushes a UInt256 whose fields are in little-endian word order (u0 least significant).
    /// The stack layout is little-endian as well.
    /// </summary>
    public void PushUInt256<TTracingInst>(in UInt256 value)
        where TTracingInst : struct, IFlag
    {
        ref Word head = ref PushedHead();

        if (BitConverter.IsLittleEndian)
        {
            // UInt256 is (u0, u1, u2, u3) from least to most significant.
            head = Vector256.Create(value.u0, value.u1, value.u2, value.u3).AsByte();
        }
        else
        {
            // Reverse individual words to keep overall little-endian semantics.
            ulong u0 = BinaryPrimitives.ReverseEndianness(value.u0);
            ulong u1 = BinaryPrimitives.ReverseEndianness(value.u1);
            ulong u2 = BinaryPrimitives.ReverseEndianness(value.u2);
            ulong u3 = BinaryPrimitives.ReverseEndianness(value.u3);
            head = Vector256.Create(u0, u1, u2, u3).AsByte();
        }

        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<Word, byte>(ref head), WordSize));
        }
    }

    public void PushSignedInt256<TTracingInst>(in Int256.Int256 value)
        where TTracingInst : struct, IFlag
    {
        // tail call into UInt256
        PushUInt256<TTracingInst>(in Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopLimbo()
    {
        if (Head-- == 0)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Pops a UInt256 from the stack, stored in little-endian layout.
    /// </summary>
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes))
        {
            return false;
        }

        // We store four ulongs in little-endian word order.
        ulong u0 = Unsafe.ReadUnaligned<ulong>(ref bytes);
        ulong u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong)));
        ulong u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong)));
        ulong u3 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong)));

        if (!BitConverter.IsLittleEndian)
        {
            u0 = BinaryPrimitives.ReverseEndianness(u0);
            u1 = BinaryPrimitives.ReverseEndianness(u1);
            u2 = BinaryPrimitives.ReverseEndianness(u2);
            u3 = BinaryPrimitives.ReverseEndianness(u3);
        }

        result = new UInt256(u0, u1, u2, u3);

        return true;
    }

    public readonly bool PeekUInt256IsZero()
    {
        int head = Head;
        if (head-- == 0)
        {
            return false;
        }

        ref byte bytes = ref _bytes[head * WordSize];

        // Little-endian layout: just read the stored UInt256.
        return Unsafe.ReadUnaligned<UInt256>(ref bytes).IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly ref byte PeekBytesByRef()
    {
        int head = Head;
        if (head-- == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }

        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
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

    public Address? PopAddress()
    {
        if (Head-- == 0)
        {
            return null;
        }

        // Stack stores address in low 20 bytes, little-endian.
        ReadOnlySpan<byte> word = _bytes.Slice(Head * WordSize, WordSize);

        byte[] addressBytes = new byte[AddressSize];
        for (int i = 0; i < AddressSize; i++)
        {
            // Convert back to big-endian representation expected by Address.
            addressBytes[i] = word[AddressSize - 1 - i];
        }

        return new Address(addressBytes);
    }

    public bool PopAddress(out Address address)
    {
        if (Head-- == 0)
        {
            address = null;
            return false;
        }

        // Stack stores address in low 20 bytes, little-endian.
        ReadOnlySpan<byte> word = _bytes.Slice(Head * WordSize, WordSize);

        byte[] addressBytes = new byte[AddressSize];
        for (int i = 0; i < AddressSize; i++)
        {
            // Convert back to big-endian representation expected by Address.
            addressBytes[i] = word[AddressSize - 1 - i];
        }

        address = new Address(addressBytes);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PopBytesByRef()
    {
        int head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }

        head--;
        Head = head;
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    public Span<byte> PopWord256()
    {
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes))
        {
            ThrowEvmStackUnderflowException();
        }

        return MemoryMarshal.CreateSpan(ref bytes, WordSize);
    }

    public Span<byte> PopWord256AsBigEndian()
    {
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes))
        {
            ThrowEvmStackUnderflowException();
        }

        var word = Unsafe.As<byte, UInt256>(ref bytes);
        return word.ToBigEndian();
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

        if (Unsafe.IsNullRef(ref bytes))
        {
            ThrowEvmStackUnderflowException();
        }

        // Little-endian: LSB is at offset 0.
        return bytes;
    }

    [SkipLocalsInit]
    public EvmExceptionType Dup<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        int head = Head;
        if (head < depth)
        {
            goto StackUnderflow;
        }

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte from = ref Unsafe.Add(ref bytes, (head - depth) * WordSize);
        ref byte to = ref Unsafe.Add(ref bytes, head * WordSize);

        Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));

        if (TTracingInst.IsActive)
        {
            Trace(depth);
        }

        if (++head >= MaxStackSize)
        {
            goto StackOverflow;
        }

        Head = head;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StackOverflow:
        return EvmExceptionType.StackOverflow;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    [SkipLocalsInit]
    public readonly EvmExceptionType Swap<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        int head = Head;
        if (head < depth)
        {
            goto StackUnderflow;
        }

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte bottom = ref Unsafe.Add(ref bytes, (head - depth) * WordSize);
        ref byte top = ref Unsafe.Add(ref bytes, (head - 1) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<Word>(ref top));
        Unsafe.WriteUnaligned(ref top, buffer);

        if (TTracingInst.IsActive)
        {
            Trace(depth);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    public readonly bool Exchange<TTracingInst>(int n, int m)
        where TTracingInst : struct, IFlag
    {
        int maxDepth = Math.Max(n, m);
        if (!EnsureDepth(maxDepth))
        {
            return false;
        }

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte first = ref Unsafe.Add(ref bytes, (Head - n) * WordSize);
        ref byte second = ref Unsafe.Add(ref bytes, (Head - m) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref first);
        Unsafe.WriteUnaligned(ref first, Unsafe.ReadUnaligned<Word>(ref second));
        Unsafe.WriteUnaligned(ref second, buffer);

        if (TTracingInst.IsActive)
        {
            Trace(maxDepth);
        }

        return true;
    }

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
