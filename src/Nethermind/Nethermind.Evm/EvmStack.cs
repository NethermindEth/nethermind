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
using System.Numerics;
using System.Runtime.Intrinsics.X86;
using Microsoft.ClearScript.Util.Web;
using Nethermind.Core.Extensions;
using Newtonsoft.Json.Converters;

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

    public EvmStack(scoped in int head, ITxTracer txTracer, scoped in Span<Word> words)
    {
        Head = head;
        _tracer = txTracer;
        _words = words;
    }

    private readonly ITxTracer _tracer;
    private readonly Span<Word> _words;
    public int Head;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Word PushRef()
    {
        // Workhorse method
        int head = Head;
        if ((Head = head + 1) >= MaxStackSize)
        {
            EvmStack.ThrowEvmStackOverflowException();
        }

        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_words), head * WordSize);
    }

    public void PushBytes(scoped ReadOnlySpan<byte> value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        PushRef() = new UInt256(value, true);
    }

    public void PushBytes(scoped in ZeroPaddedSpan value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref byte bytes = ref PushRef();
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

        ref byte bytes = ref PushRef();
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
        PushRef() = new UInt256(value);
    }

    public void PushOne()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(Bytes.OneByteSpan);
        PushRef() = UInt256.One;
    }

    public void PushZero()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(Bytes.ZeroByteSpan);
        PushRef() = UInt256.Zero;
    }

    public void PushUInt32(int value)
    {
        // Little only for now
        PushRef() = new UInt256(unchecked((uint)BinaryPrimitives.ReverseEndianness(value)));

        // TODO: trace
        //if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(intPlace);
    }

    /// <summary>
    /// Pushes an Uint256 written in big endian.
    /// </summary>
    /// <remarks>
    /// This method is a counterpart to <see cref="PopUInt256"/> and uses the same, raw data approach to write data back.
    /// </remarks>
    public void PushUInt256(in UInt256 value)
    {
        PushRef() = value;
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);
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
    /// This method does its own calculations to create the <paramref name="result"/>. It knows that 32 bytes were popped with <see cref="PopRef"/>. It doesn't have to check the size of span or slice it.
    /// All it does is <see cref="Unsafe.ReadUnaligned{T}(ref byte)"/> and then reverse endianness if needed. Then it creates <paramref name="result"/>.
    /// </remarks>
    /// <param name="result">The returned value.</param>
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref Word v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) return false;
        result = Unsafe.As<Word, UInt256>(ref v);
        return true;

        // if (Avx2.IsSupported)
        // {
        //     Word data = Unsafe.ReadUnaligned<Word>(ref bytes);
        //     Word shuffle = Vector256.Create(
        //         0x18191a1b1c1d1e1ful,
        //         0x1011121314151617ul,
        //         0x08090a0b0c0d0e0ful,
        //         0x0001020304050607ul).AsByte();
        //     if (Avx512Vbmi.VL.IsSupported)
        //     {
        //         Word convert = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
        //         result = Unsafe.As<Word, UInt256>(ref convert);
        //     }
        //     else
        //     {
        //         Word convert = Avx2.Shuffle(data, shuffle);
        //         Vector256<ulong> permute = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
        //         result = Unsafe.As<Vector256<ulong>, UInt256>(ref permute);
        //     }
        // }
        // else
        // {
        //     ulong u3, u2, u1, u0;
        //     if (BitConverter.IsLittleEndian)
        //     {
        //         // Combine read and switch endianness to movbe reg, mem
        //         u3 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref bytes));
        //         u2 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong))));
        //         u1 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong))));
        //         u0 = BinaryPrimitives.ReverseEndianness(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong))));
        //     }
        //     else
        //     {
        //         u3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
        //         u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong)));
        //         u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong)));
        //         u0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong)));
        //     }
        //
        //     result = new UInt256(u0, u1, u2, u3);
        // }

        return true;
    }

    public readonly bool PeekUInt256IsZero()
    {
        int head = Head;
        if (head-- == 0)
        {
            return false;
        }

        return _words[head].IsZero;
    }

    public readonly Span<byte> PeekWord256(out UInt256 destination)
    {
        int head = Head;
        if (head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        return ReshuffleToBigEndian(ref _words[head], ref destination);
    }

    public Address? PopAddress() => Head-- == 0
        ? null
        : new Address(_bytes.Slice(Head * WordSize + WordSize - AddressSize, AddressSize).ToArray());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref UInt256 PopRef()
    {
        int head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<UInt256>();
        }

        head--;
        Head = head;
        return ref Unsafe.Add(ref MemoryMarshal.GetReference(_words), head);
    }

    public Span<byte> PopWord256(out UInt256 destination)
    {
        ref UInt256 v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) EvmStack.ThrowEvmStackUnderflowException();

        return ReshuffleToBigEndian(ref v, ref destination);
    }

    private static Span<byte> ReshuffleToBigEndian(ref UInt256 from, ref Word to)
    {
        if (Avx2.IsSupported)
        {
            Word data = Unsafe.As<UInt256, Word>(ref from);
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();

            if (Avx512Vbmi.VL.IsSupported)
            {
                to = Avx512Vbmi.VL.PermuteVar32x8(data, shuffle);
            }
            else
            {
                Word convert = Avx2.Shuffle(data, shuffle);
                Vector256<ulong> permute =
                    Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);

                result = Unsafe.As<Vector256<ulong>, UInt256>(ref permute);
            }
        }
        else
        {
            throw new NotImplementedException();
        }
    }

    public byte PopByte()
    {
        ref UInt256 v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) EvmStack.ThrowEvmStackUnderflowException();

        if (BitConverter.IsLittleEndian == false)
            throw new Exception();

        // return the smallest
        return (byte)(v.u0 & 0xFF);
    }

    public bool Dup(in int depth)
    {
        if (!EnsureDepth(depth)) return false;

        ref UInt256 v = ref MemoryMarshal.GetReference(_words);

        ref UInt256 from = ref Unsafe.Add(ref v, Head - depth);
        ref UInt256 to = ref Unsafe.Add(ref v, Head );

        to = from;

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

        ref UInt256 v = ref MemoryMarshal.GetReference(_words);

        ref UInt256 bottom = ref Unsafe.Add(ref v, Head - depth);
        ref UInt256 top = ref Unsafe.Add(ref v, Head );

        UInt256 buffer = bottom;
        bottom = top;
        top = buffer;

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
            _tracer.ReportStackPush(_words[Head - i]);
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
