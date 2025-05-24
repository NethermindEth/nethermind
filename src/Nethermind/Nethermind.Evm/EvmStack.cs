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
using static Unsafe;

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
        _tracer = txTracer.IsTracingInstructions ? txTracer : null;
        _bytes = bytes;
    }

    private readonly ITxTracer? _tracer;
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

        return ref Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    public void PushBytes(scoped ReadOnlySpan<byte> value)
    {
        _tracer?.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        if (value.Length != WordSize)
        {
            // Not full entry, clear first
            As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Add(ref bytes, WordSize - value.Length), value.Length));
        }
        else
        {
            As<byte, Word>(ref bytes) = As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }
    }

    public void PushBytes(scoped in ZeroPaddedSpan value)
    {
        _tracer?.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length != WordSize)
        {
            // Not full entry, clear first
            As<byte, Word>(ref bytes) = default;
            valueSpan.CopyTo(MemoryMarshal.CreateSpan(ref bytes, value.Length));
        }
        else
        {
            As<byte, Word>(ref bytes) = As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan));
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushByte(byte value)
    {
        _tracer?.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        // Not full entry, clear first
        As<byte, Word>(ref bytes) = default;
        Add(ref bytes, WordSize - sizeof(byte)) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push2Bytes(ref byte value)
    {
        // ushort size
        _tracer?.TraceBytes(in value, sizeof(ushort));

        ref byte bytes = ref PushBytesRef();

        // Clear 32 bytes
        As<byte, Word>(ref bytes) = default;

        // Copy 2 bytes
        As<byte, ushort>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong) + sizeof(uint) + sizeof(ushort)))
            = As<byte, ushort>(ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push4Bytes(ref byte value)
    {
        // uint size
        _tracer?.TraceBytes(in value, sizeof(uint));

        ref byte bytes = ref PushBytesRef();

        // First 16+8+4 bytes are zero
        As<byte, HalfWord>(ref bytes) = default;
        As<byte, ulong>(ref Add(ref bytes, sizeof(HalfWord))) = default;
        As<byte, uint>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong))) = default;

        // Copy 4 bytes
        As<byte, uint>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong) + sizeof(uint)))
            = As<byte, uint>(ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push8Bytes(ref byte value)
    {
        // ulong size
        _tracer?.TraceBytes(in value, sizeof(ulong));

        ref byte bytes = ref PushBytesRef();

        // First 16+8 bytes are zero
        As<byte, HalfWord>(ref bytes) = default;
        As<byte, ulong>(ref Add(ref bytes, sizeof(HalfWord))) = default;

        // Copy 8 bytes
        As<byte, ulong>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong)))
            = As<byte, ulong>(ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void Push16Bytes(ref byte value)
    {
        // UInt128 size
        _tracer?.TraceBytes(in value, sizeof(HalfWord));

        ref byte bytes = ref PushBytesRef();

        // First 16 bytes are zero
        As<byte, HalfWord>(ref bytes) = default;

        // Copy 16 bytes
        As<byte, HalfWord>(ref Add(ref bytes, sizeof(HalfWord)))
            = As<byte, HalfWord>(ref value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push20Bytes(ref byte value)
    {
        // Address size
        _tracer?.TraceBytes(in value, 20);

        ref byte bytes = ref PushBytesRef();

        // First 4+8 bytes are zero
        As<byte, ulong>(ref bytes) = 0;
        As<byte, uint>(ref Add(ref bytes, sizeof(ulong))) = 0;

        // 20 bytes which is uint+Vector128
        As<byte, uint>(ref Add(ref bytes, sizeof(uint) + sizeof(ulong)))
            = As<byte, uint>(ref value);

        As<byte, HalfWord>(ref Add(ref bytes, sizeof(ulong) + sizeof(ulong)))
            = As<byte, HalfWord>(ref Add(ref value, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushAddress(Address address)
    {
        ref byte value = ref MemoryMarshal.GetArrayDataReference(address.Bytes);
        // Address size
        _tracer?.TraceBytes(in value, 20);

        ref byte bytes = ref PushBytesRef();

        // First 4+8 bytes are zero
        As<byte, ulong>(ref bytes) = 0;
        As<byte, uint>(ref Add(ref bytes, sizeof(ulong))) = 0;

        // 20 bytes which is uint+Vector128
        As<byte, uint>(ref Add(ref bytes, sizeof(uint) + sizeof(ulong)))
            = As<byte, uint>(ref value);

        As<byte, HalfWord>(ref Add(ref bytes, sizeof(ulong) + sizeof(ulong)))
            = As<byte, HalfWord>(ref Add(ref value, sizeof(uint)));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes(in Word value)
    {
        _tracer?.TraceWord(in value);

        ref byte bytes = ref PushBytesRef();
        As<byte, Word>(ref bytes) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push32Bytes(in ValueHash256 hash)
    {
        ref readonly Word value = ref As<ValueHash256, Word>(ref AsRef(in hash));

        _tracer?.TraceWord(in value);

        ref byte bytes = ref PushBytesRef();
        As<byte, Word>(ref bytes) = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PushLeftPaddedBytes(ReadOnlySpan<byte> value, int paddingLength)
    {
        _tracer?.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        if (value.Length != WordSize)
        {
            // Not full entry, clear first
            As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Add(ref bytes, WordSize - paddingLength), value.Length));
        }
        else
        {
            As<byte, Word>(ref bytes) = As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }
    }

    public void PushOne()
    {
        _tracer?.ReportStackPush(Bytes.OneByteSpan);

        ref byte bytes = ref PushBytesRef();
        // Not full entry, clear first
        As<byte, Word>(ref bytes) = default;
        Add(ref bytes, WordSize - sizeof(byte)) = 1;
    }

    public void PushZero()
    {
        _tracer?.ReportStackPush(Bytes.ZeroByteSpan);

        ref byte bytes = ref PushBytesRef();
        As<byte, Word>(ref bytes) = default;
    }

    public unsafe void PushUInt32(uint value)
    {
        if (BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        // uint size
        _tracer?.TraceBytes(in As<uint, byte>(ref value), sizeof(uint));

        ref byte bytes = ref PushBytesRef();
        // First 16+8+4 bytes are zero
        As<byte, HalfWord>(ref bytes) = default;
        As<byte, ulong>(ref Add(ref bytes, sizeof(HalfWord))) = default;
        As<byte, uint>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong))) = default;

        // Copy 4 bytes
        As<byte, uint>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong) + sizeof(uint)))
            = value;
    }

    public unsafe void PushUInt64(ulong value)
    {
        if (BitConverter.IsLittleEndian)
        {
            value = BinaryPrimitives.ReverseEndianness(value);
        }
        // uint size
        _tracer?.TraceBytes(in As<ulong, byte>(ref value), sizeof(ulong));

        ref byte bytes = ref PushBytesRef();
        // First 16+8 bytes are zero
        As<byte, HalfWord>(ref bytes) = default;
        As<byte, ulong>(ref Add(ref bytes, sizeof(HalfWord))) = default;

        // Copy 8 bytes
        As<byte, ulong>(ref Add(ref bytes, sizeof(HalfWord) + sizeof(ulong)))
            = value;
    }

    /// <summary>
    /// Pushes an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method is a counterpart to <see cref="PopUInt256"/> and uses the same, raw data approach to write data back.
    /// </remarks>
    public void PushUInt256(in UInt256 value)
    {
        ref byte bytes = ref PushBytesRef();
        if (Avx2.IsSupported)
        {
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word data = As<UInt256, Word>(ref AsRef(in value));
                WriteUnaligned(ref bytes, Avx512Vbmi.VL.PermuteVar32x8(data, shuffle));
            }
            else if (Avx2.IsSupported)
            {
                Vector256<ulong> permute = As<UInt256, Vector256<ulong>>(ref AsRef(in value));
                Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
                WriteUnaligned(ref bytes, Avx2.Shuffle(As<Vector256<ulong>, Word>(ref convert), shuffle));
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

            WriteUnaligned(ref bytes, Vector256.Create(u3, u2, u1, u0));
        }

        _tracer?.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref bytes, WordSize));
    }

    public void PushSignedInt256(in Int256.Int256 value)
    {
        // tail call into UInt256
        PushUInt256(in As<Int256.Int256, UInt256>(ref AsRef(in value)));
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
        SkipInit(out result);
        ref byte bytes = ref PopBytesByRef();
        if (IsNullRef(ref bytes)) return false;

        if (Avx2.IsSupported)
        {
            Word data = ReadUnaligned<Word>(ref bytes);
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                Word convert = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
                result = As<Word, UInt256>(ref convert);
            }
            else
            {
                Word convert = Avx2.Shuffle(data, shuffle);
                Vector256<ulong> permute = Avx2.Permute4x64(As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
                result = As<Vector256<ulong>, UInt256>(ref permute);
            }
        }
        else
        {
            ulong u3, u2, u1, u0;
            if (BitConverter.IsLittleEndian)
            {
                // Combine read and switch endianness to movbe reg, mem
                u3 = BinaryPrimitives.ReverseEndianness(ReadUnaligned<ulong>(ref bytes));
                u2 = BinaryPrimitives.ReverseEndianness(ReadUnaligned<ulong>(ref Add(ref bytes, sizeof(ulong))));
                u1 = BinaryPrimitives.ReverseEndianness(ReadUnaligned<ulong>(ref Add(ref bytes, 2 * sizeof(ulong))));
                u0 = BinaryPrimitives.ReverseEndianness(ReadUnaligned<ulong>(ref Add(ref bytes, 3 * sizeof(ulong))));
            }
            else
            {
                u3 = ReadUnaligned<ulong>(ref bytes);
                u2 = ReadUnaligned<ulong>(ref Add(ref bytes, sizeof(ulong)));
                u1 = ReadUnaligned<ulong>(ref Add(ref bytes, 2 * sizeof(ulong)));
                u0 = ReadUnaligned<ulong>(ref Add(ref bytes, 3 * sizeof(ulong)));
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
        return ReadUnaligned<UInt256>(ref bytes).IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PeekBytesByRef()
    {
        int head = Head;
        if (head-- == 0)
        {
            return ref NullRef<byte>();
        }
        return ref Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
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
            return ref NullRef<byte>();
        }
        head--;
        Head = head;
        return ref Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    public Span<byte> PopWord256()
    {
        ref byte bytes = ref PopBytesByRef();
        if (IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

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

        if (IsNullRef(ref bytes)) ThrowEvmStackUnderflowException();

        return Add(ref bytes, WordSize - sizeof(byte));
    }

    public bool Dup(in int depth)
    {
        if (!EnsureDepth(depth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte from = ref Add(ref bytes, (Head - depth) * WordSize);
        ref byte to = ref Add(ref bytes, Head * WordSize);

        WriteUnaligned(ref to, ReadUnaligned<Word>(ref from));

        if (_tracer is not null) Trace(depth);

        if (++Head >= MaxStackSize)
        {
            ThrowEvmStackOverflowException();
        }

        return true;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    public readonly bool Swap(int depth)
    {
        if (!EnsureDepth(depth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte bottom = ref Add(ref bytes, (Head - depth) * WordSize);
        ref byte top = ref Add(ref bytes, (Head - 1) * WordSize);

        Word buffer = ReadUnaligned<Word>(ref bottom);
        WriteUnaligned(ref bottom, ReadUnaligned<Word>(ref top));
        WriteUnaligned(ref top, buffer);

        if (_tracer is not null) Trace(depth);

        return true;
    }

    public readonly bool Exchange(int n, int m)
    {
        int maxDepth = Math.Max(n, m);
        if (!EnsureDepth(maxDepth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte first = ref Add(ref bytes, (Head - n) * WordSize);
        ref byte second = ref Add(ref bytes, (Head - m) * WordSize);

        Word buffer = ReadUnaligned<Word>(ref first);
        WriteUnaligned(ref first, ReadUnaligned<Word>(ref second));
        WriteUnaligned(ref second, buffer);

        if (_tracer is not null) Trace(maxDepth);

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
