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
        => Vector256.Create(0UL, 0UL, 0UL, value).AsByte();

    public void PushBytes<TTracingInst>(scoped ReadOnlySpan<byte> value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        if (value.Length != WordSize)
        {
            ref byte bytes = ref PushBytesRef();
            // Not full entry, clear first
            Unsafe.As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - value.Length), value.Length));
        }
        else
        {
            PushedHead() = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushByte<TTracingInst>(byte value)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        // Build a 256-bit vector: [ 0, 0, 0, (value << 56) ]
        // - when viewed as bytes: all zeros except byte[31] == value
        ref Word head = ref PushedHead();
        // Single 32-byte store: last byte as value
        head = CreateWordFromUInt64((ulong)value << 56);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push2Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // ushort size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(ushort));

        ref Word head = ref PushedHead();
        // Load 2-byte source into the top 16 bits of the last 64-bit lane:
        // lane3 covers bytes [24..31], so shifting by 48 bits
        ulong lane3 = (ulong)Unsafe.As<byte, ushort>(ref value) << 48;

        // Single 32-byte store
        head = CreateWordFromUInt64(lane3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push4Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // uint size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(uint));

        ref Word head = ref PushedHead();
        // Load 4-byte source into the top 32 bits of the last 64-bit lane:
        // lane3 covers bytes [24..31], so shifting by 32 bits
        ulong lane3 = ((ulong)Unsafe.As<byte, uint>(ref value)) << 32;

        // Single 32-byte store
        head = CreateWordFromUInt64(lane3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push8Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // ulong size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(ulong));

        ref Word head = ref PushedHead();
        // Load 8-byte source into last 64-bit lane
        ulong lane3 = Unsafe.As<byte, ulong>(ref value);

        // Single 32-byte store
        head = CreateWordFromUInt64(lane3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push16Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // UInt128 size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, sizeof(HalfWord));

        ref Word head = ref PushedHead();
        // Load 16-byte source into 16-byte source as a Vector128<byte>
        HalfWord src = Unsafe.As<byte, HalfWord>(ref value);
        // Single 32-byte store
        head = Vector256.Create(default, src);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push20Bytes<TTracingInst>(ref byte value)
        where TTracingInst : struct, IFlag
    {
        // Address size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in value, 20);

        ref Word head = ref PushedHead();
        // build the 4×8-byte lanes:
        // - lane0 = 0UL
        // - lane1 = first 4 bytes of 'value', shifted up into the high half
        // - lane2 = bytes [4..11] of 'value'
        // - lane3 = bytes [12..19] of 'value'
        ulong lane1 = ((ulong)Unsafe.As<byte, uint>(ref value)) << 32;
        ulong lane2 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 4));
        ulong lane3 = Unsafe.As<byte, ulong>(ref Unsafe.Add(ref value, 12));

        head = Vector256.Create(default, lane1, lane2, lane3).AsByte();
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
            _tracer.TraceWord(in value);

        // Single 32-byte store
        PushedHead() = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes<TTracingInst>(in ValueHash256 hash)
        where TTracingInst : struct, IFlag
        => Push32Bytes<TTracingInst>(in Unsafe.As<ValueHash256, Word>(ref Unsafe.AsRef(in hash)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushLeftPaddedBytes<TTracingInst>(ReadOnlySpan<byte> value, int paddingLength)
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(value);

        if (value.Length != WordSize)
        {
            ref byte bytes = ref PushBytesRef();
            // Not full entry, clear first
            Unsafe.As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - paddingLength), value.Length));
        }
        else
        {
            PushedHead() = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushOne<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.OneByteSpan);

        // Build a 256-bit vector: [ 0, 0, 0, (1UL << 56) ]
        // - when viewed as bytes: all zeros except byte[31] == 1

        // Single 32-byte store
        PushedHead() = CreateWordFromUInt64(1UL << 56);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushZero<TTracingInst>()
        where TTracingInst : struct, IFlag
    {
        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.ZeroByteSpan);

        // Single 32-byte store: Zero 
        PushedHead() = default;
    }

    public unsafe void PushUInt32<TTracingInst>(uint value)
        where TTracingInst : struct, IFlag
    {
        if (BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        // uint size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in Unsafe.As<uint, byte>(ref value), sizeof(uint));

        // Single 32-byte store
        PushedHead() = Vector256.Create(0U, 0U, 0U, 0U, 0U, 0U, 0U, value).AsByte();
    }

    public unsafe void PushUInt64<TTracingInst>(ulong value)
        where TTracingInst : struct, IFlag
    {
        if (BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        // ulong size
        if (TTracingInst.IsActive)
            _tracer.TraceBytes(in Unsafe.As<ulong, byte>(ref value), sizeof(ulong));

        // Single 32-byte store
        PushedHead() = CreateWordFromUInt64(value);
    }

    /// <summary>
    /// Pushes an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method is a counterpart to <see cref="PopUInt256"/> and uses the same, raw data approach to write data back.
    /// </remarks>

    public void PushUInt256<TTracingInst>(in UInt256 value)
        where TTracingInst : struct, IFlag
    {
        ref Word head = ref PushedHead();
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
            else if (Avx2.IsSupported)
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
    /// Pops an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method does its own calculations to create the <paramref name="result"/>. It knows that 32 bytes were popped with <see cref="PopBytesByRef"/>. It doesn't have to check the size of span or slice it.
    /// All it does is <see cref="Unsafe.ReadUnaligned{T}(ref byte)"/> and then reverse endianness if needed. Then it creates <paramref name="result"/>.
    /// </remarks>
    /// <param name="result">The returned value.</param>
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) return false;

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
        int head = Head;
        if (head-- == 0)
        {
            return false;
        }

        ref byte bytes = ref _bytes[head * WordSize];
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

    public Address? PopAddress() => Head-- == 0 ? null : new Address(_bytes.Slice(Head * WordSize + WordSize - AddressSize, AddressSize).ToArray());

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

    public bool Dup<TTracingInst>(in int depth)
        where TTracingInst : struct, IFlag
    {
        if (!EnsureDepth(depth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte from = ref Unsafe.Add(ref bytes, (Head - depth) * WordSize);
        ref byte to = ref Unsafe.Add(ref bytes, Head * WordSize);

        Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));

        if (TTracingInst.IsActive) Trace(depth);

        if (++Head >= MaxStackSize)
        {
            ThrowEvmStackOverflowException();
        }

        return true;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    public readonly bool Swap<TTracingInst>(int depth)
        where TTracingInst : struct, IFlag
    {
        if (!EnsureDepth(depth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte bottom = ref Unsafe.Add(ref bytes, (Head - depth) * WordSize);
        ref byte top = ref Unsafe.Add(ref bytes, (Head - 1) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<Word>(ref top));
        Unsafe.WriteUnaligned(ref top, buffer);

        if (TTracingInst.IsActive) Trace(depth);

        return true;
    }

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
