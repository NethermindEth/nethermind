// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;
using System.Diagnostics;
using System.Runtime.Intrinsics.X86;

namespace Nethermind.Evm;

using static Nethermind.Evm.VirtualMachine;

using Word = Vector256<byte>;

public ref struct EvmStack<TTracing>
    where TTracing : struct, IIsTracing
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int ReturnStackSize = 1023;
    public const int WordSize = 32;
    public const int AddressSize = 20;

    public EvmStack(scoped in Span<byte> bytes, scoped in int head, ITxTracer txTracer)
    {
        _bytes = bytes;
        Head = head;
        _tracer = txTracer;
    }

    public int Head;

    private Span<byte> _bytes;

    private ITxTracer _tracer;

    public void PushBytes(scoped in Span<byte> value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        if (value.Length != WordSize)
        {
            ClearWordAtHead();
            value.CopyTo(_bytes.Slice(Head * WordSize + WordSize - value.Length, value.Length));
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize), Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value)));
        }

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    public void PushBytes(scoped in ZeroPaddedSpan value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length != WordSize)
        {
            ClearWordAtHead();
            Span<byte> stack = _bytes.Slice(Head * WordSize, valueSpan.Length);
            valueSpan.CopyTo(stack);
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize), Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan)));
        }

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    public ref byte PushBytesRef()
    {
        ref byte bytes = ref _bytes[Head * WordSize];

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }

        return ref bytes;
    }

    public void PushByte(byte value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ClearWordAtHead();
        _bytes[Head * WordSize + WordSize - sizeof(byte)] = value;

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    private static ReadOnlySpan<byte> OneStackItem() => new byte[] { 1 };

    public void PushOne()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(OneStackItem());

        ClearWordAtHead();
        _bytes[Head * WordSize + WordSize - sizeof(byte)] = 1;

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    private static ReadOnlySpan<byte> ZeroStackItem() => new byte[] { 0 };

    public void PushZero()
    {
        if (typeof(TTracing) == typeof(IsTracing))
        {
            _tracer.ReportStackPush(ZeroStackItem());
        }

        ClearWordAtHead();

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    public void PushUInt32(in int value)
    {
        ClearWordAtHead();

        Span<byte> intPlace = _bytes.Slice(Head * WordSize + WordSize - sizeof(uint), sizeof(uint));
        BinaryPrimitives.WriteInt32BigEndian(intPlace, value);

        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(intPlace);

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    /// <summary>
    /// Pushes an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method is a counterpart to <see cref="PopUInt256"/> and uses the same, raw data approach to write data back.
    /// </remarks>
    public void PushUInt256(in UInt256 value)
    {
        Span<byte> word = _bytes.Slice(Head * WordSize, WordSize);
        ref byte bytes = ref MemoryMarshal.GetReference(word);

        if (Avx2.IsSupported)
        {
            Vector256<ulong> permute = Unsafe.As<UInt256, Vector256<ulong>>(ref Unsafe.AsRef(value));
            Vector256<ulong> convert = Avx2.Permute4x64(permute, 0b_01_00_11_10);
            Word shuffle = Vector256.Create(
                (byte)
                31, 30, 29, 28, 27, 26, 25, 24,
                23, 22, 21, 20, 19, 18, 17, 16,
                15, 14, 13, 12, 11, 10, 9, 8,
                7, 6, 5, 4, 3, 2, 1, 0);
            Unsafe.WriteUnaligned(ref bytes, Avx2.Shuffle(Unsafe.As<Vector256<ulong>, Word>(ref convert), shuffle));
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

        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(word);

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    public void PushSignedInt256(in Int256.Int256 value)
    {
        // tail call into UInt256
        PushUInt256(Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));
    }

    public void PopLimbo()
    {
        if (Head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }
    }

    public void PopSignedInt256(out Int256.Int256 result)
    {
        // tail call into UInt256
        Unsafe.SkipInit(out result);
        PopUInt256(out Unsafe.As<Int256.Int256, UInt256>(ref result));
    }

    /// <summary>
    /// Pops an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method does its own calculations to create the <paramref name="result"/>. It knows that 32 bytes were popped with <see cref="PopBytesByRef"/>. It doesn't have to check the size of span or slice it.
    /// All it does is <see cref="Unsafe.ReadUnaligned{T}(ref byte)"/> and then reverse endianness if needed. Then it creates <paramref name="result"/>.
    /// </remarks>
    /// <param name="result">The returned value.</param>
    public void PopUInt256(out UInt256 result)
    {
        ref byte bytes = ref PopBytesByRef();

        if (Avx2.IsSupported)
        {
            Word data = Unsafe.ReadUnaligned<Word>(ref bytes);
            Word shuffle = Vector256.Create(
                (byte)
                31, 30, 29, 28, 27, 26, 25, 24,
                23, 22, 21, 20, 19, 18, 17, 16,
                15, 14, 13, 12, 11, 10, 9, 8,
                7, 6, 5, 4, 3, 2, 1, 0);
            Word convert = Avx2.Shuffle(data, shuffle);
            Vector256<ulong> permute = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
            result = Unsafe.As<Vector256<ulong>, UInt256>(ref permute);
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
    }

    public bool PeekUInt256IsZero()
    {
        int head = Head;
        if (head-- == 0)
        {
            return false;
        }

        ref byte bytes = ref _bytes[head * WordSize];
        return Unsafe.ReadUnaligned<UInt256>(ref bytes).IsZero;
    }

    public Span<byte> PeekWord256()
    {
        int head = Head;
        if (head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return _bytes.Slice(head * WordSize, WordSize);
    }

    public Address PopAddress()
    {
        if (Head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return new Address(_bytes.Slice(Head * WordSize + WordSize - AddressSize, AddressSize).ToArray());
    }

    public ref byte PopBytesByRef()
    {
        if (Head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return ref _bytes[Head * WordSize];
    }

    public Span<byte> PopWord256()
    {
        if (Head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return _bytes.Slice(Head * WordSize, WordSize);
    }

    public byte PopByte()
    {
        if (Head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return _bytes[Head * WordSize + WordSize - sizeof(byte)];
    }

    public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        if (value.Length != WordSize)
        {
            ClearWordAtHead();
            value.CopyTo(_bytes.Slice(Head * WordSize + WordSize - paddingLength, value.Length));
        }
        else
        {
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize), Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value)));
        }

        if (++Head >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }
    }

    public void Dup(in int depth)
    {
        EnsureDepth(depth);

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
    }

    public void EnsureDepth(int depth)
    {
        if (Head < depth)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }
    }

    public void Swap(int depth)
    {
        EnsureDepth(depth);

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
    }

    private readonly void Trace(int depth)
    {
        for (int i = depth; i > 0; i--)
        {
            _tracer.ReportStackPush(_bytes.Slice(Head * WordSize - i * WordSize, WordSize));
        }
    }

    public List<string> GetStackTrace()
    {
        List<string> stackTrace = new();
        for (int i = 0; i < Head; i++)
        {
            Span<byte> stackItem = _bytes.Slice(i * WordSize, WordSize);
            stackTrace.Add(stackItem.ToArray().ToHexString(true, true));
        }

        return stackTrace;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ClearWordAtHead()
    {
        Unsafe.WriteUnaligned(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize), Word.Zero);
    }
}

public static class EvmStack
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int ReturnStackSize = 1023;

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
