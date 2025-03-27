// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Extensions;
using BytesExt = Nethermind.Core.Extensions.Bytes;

namespace Nethermind.Evm;

using static VirtualMachine;
using Word = Vector256<byte>;

[StructLayout(LayoutKind.Auto)]
public ref struct EvmStack<TTracing>
    where TTracing : struct, IIsTracing
{
    public const int MaxStackSize = EvmStack.MaxStackSize;
    public const int WordSize = EvmStack.WordSize;
    public const int AddressSize = EvmStack.AddressSize;

    public ref byte HeadRef => ref _bytes[Head * EvmStack.WordSize];

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
            EvmStack.ThrowEvmStackOverflowException();
        }

        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), head * WordSize);
    }

    public void PushBytes(scoped ReadOnlySpan<byte> value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        if (value.Length != WordSize)
        {
            // Not full entry, clear first
            Unsafe.As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - value.Length), value.Length));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }
    }

    public void PushBytes(scoped in ZeroPaddedSpan value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length != WordSize)
        {
            // Not full entry, clear first
            Unsafe.As<byte, Word>(ref bytes) = default;
            valueSpan.CopyTo(MemoryMarshal.CreateSpan(ref bytes, value.Length));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan));
        }
    }

    public void PushLeftPaddedBytes(ReadOnlySpan<byte> value, int paddingLength)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        if (value.Length != WordSize)
        {
            // Not full entry, clear first
            Unsafe.As<byte, Word>(ref bytes) = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - paddingLength), value.Length));
        }
        else
        {
            Unsafe.As<byte, Word>(ref bytes) = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }
    }

    public void PushByte(byte value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref byte bytes = ref PushBytesRef();
        // Not full entry, clear first
        Unsafe.As<byte, Word>(ref bytes) = default;
        Unsafe.Add(ref bytes, WordSize - sizeof(byte)) = value;
    }

    public void PushOne()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(BytesExt.OneByteSpan);

        ref byte bytes = ref PushBytesRef();
        // Not full entry, clear first
        Unsafe.As<byte, Word>(ref bytes) = default;
        Unsafe.Add(ref bytes, WordSize - sizeof(byte)) = 1;
    }

    public void PushZero()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(BytesExt.ZeroByteSpan);

        ref byte bytes = ref PushBytesRef();
        Unsafe.As<byte, Word>(ref bytes) = default;
    }

    public void PushUInt32(in int value)
    {
        ref byte bytes = ref PushBytesRef();
        // Not full entry, clear first
        Unsafe.As<byte, Word>(ref bytes) = default;

        Span<byte> intPlace = MemoryMarshal.CreateSpan(ref Unsafe.Add(ref bytes, WordSize - sizeof(uint)), sizeof(uint));
        BinaryPrimitives.WriteInt32BigEndian(intPlace, value);

        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(intPlace);
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
                Word data = Unsafe.As<UInt256, Word>(ref Unsafe.AsRef(in value));
                Unsafe.WriteUnaligned(ref bytes, Avx512Vbmi.VL.PermuteVar32x8(data, shuffle));
            }
            else if (Avx2.IsSupported)
            {
                Vector256<ulong> permute = Unsafe.As<UInt256, Vector256<ulong>>(ref Unsafe.AsRef(in value));
                Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
                Unsafe.WriteUnaligned(ref bytes, Avx2.Shuffle(Unsafe.As<Vector256<ulong>, Word>(ref convert), shuffle));
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

            Unsafe.WriteUnaligned(ref bytes, Vector256.Create(u3, u2, u1, u0));
        }

        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref bytes, WordSize));
    }

    public void PushSignedInt256(in Int256.Int256 value)
    {
        // tail call into UInt256
        PushUInt256(in Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));
    }

    public void PopLimbo()
    {
        if (Head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }
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

    public readonly Span<byte> PeekWord256()
    {
        int head = Head;
        if (head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return _bytes.Slice(head * WordSize, WordSize);
    }

    public Address? PopAddress() => Head-- == 0 ? null : new Address(_bytes.Slice(Head * WordSize + WordSize - AddressSize, AddressSize).ToArray());

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
        if (Unsafe.IsNullRef(ref bytes)) EvmStack.ThrowEvmStackUnderflowException();

        return MemoryMarshal.CreateSpan(ref bytes, WordSize);
    }

    public byte PopByte()
    {
        ref byte bytes = ref PopBytesByRef();
        if (Unsafe.IsNullRef(ref bytes)) EvmStack.ThrowEvmStackUnderflowException();

        return Unsafe.Add(ref bytes, WordSize - sizeof(byte));
    }

    public bool Dup(in int depth)
    {
        if (!EnsureDepth(depth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte from = ref Unsafe.Add(ref bytes, (Head - depth) * WordSize);
        ref byte to = ref Unsafe.Add(ref bytes, Head * WordSize);

        Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));

        if (typeof(TTracing) == typeof(IsTracing))
        {
            Trace(depth);
        }

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }

        return true;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    public readonly bool Swap(int depth)
    {
        if (!EnsureDepth(depth)) return false;

        ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

        ref byte bottom = ref Unsafe.Add(ref bytes, (Head - depth) * WordSize);
        ref byte top = ref Unsafe.Add(ref bytes, (Head - 1) * WordSize);

        Word buffer = Unsafe.ReadUnaligned<Word>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<Word>(ref top));
        Unsafe.WriteUnaligned(ref top, buffer);

        if (typeof(TTracing) == typeof(IsTracing))
        {
            Trace(depth);
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
}

public static class EvmStack
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int ReturnStackSize = 1023;
    public const int WordSize = 32;
    public const int AddressSize = 20;

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
