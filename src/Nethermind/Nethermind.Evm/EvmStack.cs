// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
using static Nethermind.Evm.Endianness;

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

        return ref _words[head];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref UInt256 PushRefAsUInt256() => ref Unsafe.As<Word, UInt256>(ref PushRef());

    public void PushBytes(scoped ReadOnlySpan<byte> value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref Word top = ref PushRef();
        if (value.Length != WordSize)
        {
            // Not full entry, clear first.
            top = default;
            var offset = WordSize - value.Length;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref top), offset),
                value.Length));
        }
        else
        {
            top = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }

        Reshuffle(ref top);
    }

    public void PushBytes(scoped in ZeroPaddedSpan value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref Word top = ref PushRef();
        ReadOnlySpan<byte> valueSpan = value.Span;
        if (valueSpan.Length != WordSize)
        {
            // Not full entry, clear first.
            top = default;
            valueSpan.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref top), value.Length));
        }
        else
        {
            top = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(valueSpan));
        }

        // Reshuffle top
        Reshuffle(ref top);
    }

    public void PushLeftPaddedBytes(ReadOnlySpan<byte> value, int paddingLength)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        ref Word top = ref PushRef();
        if (value.Length != WordSize)
        {
            // Not full entry, clear first
            top = default;
            value.CopyTo(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref top), WordSize - paddingLength), value.Length));
        }
        else
        {
            top = Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value));
        }

        Reshuffle(ref top);
    }

    public void PushByte(byte value)
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(value);

        PushRefAsUInt256() = new UInt256(value);
    }

    public void PushOne()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(Bytes.OneByteSpan);

        PushRefAsUInt256() = UInt256.One;
    }

    public void PushZero()
    {
        if (typeof(TTracing) == typeof(IsTracing)) _tracer.ReportStackPush(Bytes.ZeroByteSpan);
        PushRef() = default;
    }

    public void PushUInt32(int value)
    {
        ref Word top = ref PushRef();
        Unsafe.As<Word, UInt256>(ref top) = new UInt256(unchecked((uint)value));

        if (typeof(TTracing) == typeof(IsTracing))
        {
            Word traced = top;
            Reshuffle(ref traced);

            Span<byte> span = MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref traced), WordSize);
            _tracer.ReportStackPush(span);
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
        PushRefAsUInt256() = value;

        if (typeof(TTracing) == typeof(IsTracing))
        {
            Word trace = Unsafe.As<UInt256, Word>(ref Unsafe.AsRef(in value));
            Reshuffle(ref trace);
            _tracer.ReportStackPush(MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref trace), WordSize));
        }
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
    /// Pops an <see cref="UInt256"/>.
    /// </summary>
    /// <param name="result">The returned value.</param>
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref Word v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) return false;
        result = Unsafe.As<Word, UInt256>(ref v);
        return true;
    }

    /// <summary>
    /// Peeks a reference to an <see cref="UInt256"/>.
    /// </summary>
    public ref UInt256 PeekUInt256Ref()
    {
        int head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<UInt256>();
        }

        return ref Unsafe.As<Word, UInt256>(ref _words[head - 1]);
    }

    public readonly bool PeekUInt256IsZero()
    {
        int head = Head;
        if (head-- == 0)
        {
            return false;
        }

        return _words[head] == Word.Zero;
    }

    public readonly Span<byte> PeekWord256(out UInt256 destination)
    {
        int head = Head;
        if (head-- == 0)
        {
            EvmStack.ThrowEvmStackUnderflowException();
        }

        Unsafe.SkipInit(out destination);
        ref Word word = ref Unsafe.As<UInt256, Word>(ref destination);
        word = _words[head];
        Reshuffle(ref word);
        return MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref word), WordSize);
    }

    public Address? PopAddress()
    {
        if (Head-- == 0)
            return null;

        ref Word word = ref _words[Head];
        Reshuffle(ref word);

        const int offset = WordSize - AddressSize;
        return new Address(MemoryMarshal
            .CreateSpan(ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref word), offset), AddressSize)
            .ToArray());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref Word PopRef()
    {
        int head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<Word>();
        }

        head--;
        Head = head;
        return ref _words[head];
    }

    public ref Word PeekRef()
    {
        int head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<Word>();
        }

        return ref _words[head - 1];
    }

    public Span<byte> PopWord(out UInt256 destination)
    {
        ref Word v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) EvmStack.ThrowEvmStackUnderflowException();

        Unsafe.SkipInit(out destination);
        ref Word dest = ref Unsafe.As<UInt256, Word>(ref destination);

        dest = v;
        Reshuffle(ref dest);
        return MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref dest), WordSize);
    }

    /// <summary>
    /// Pops the word using the stack slot without copying.
    /// This means that if there's a follow push without consuming the value, it will be overwritten.
    /// </summary>
    public Span<byte> PopWordUnsafe()
    {
        ref Word v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) EvmStack.ThrowEvmStackUnderflowException();

        Reshuffle(ref v);
        return MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref v), WordSize);
    }

    public byte PopByte()
    {
        ref Word v = ref PopRef();
        if (Unsafe.IsNullRef(ref v)) EvmStack.ThrowEvmStackUnderflowException();

        // Return the smallest
        return v[BitConverter.IsLittleEndian ? 0 : (WordSize - 1)];
    }

    public bool Dup(in int depth)
    {
        if (!EnsureDepth(depth)) return false;

        ref Word v = ref MemoryMarshal.GetReference(_words);

        ref Word from = ref Unsafe.Add(ref v, Head - depth);
        ref Word to = ref Unsafe.Add(ref v, Head);

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

        ref Word v = ref MemoryMarshal.GetReference(_words);

        ref Word bottom = ref Unsafe.Add(ref v, Head - depth);
        ref Word top = ref Unsafe.Add(ref v, Head);

        Word buffer = bottom;
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
            Word trace = _words[Head - i];
            Reshuffle(ref trace);
            _tracer.ReportStackPush(MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref trace), WordSize));
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

public static class Endianness
{
    public static void Reshuffle(ref Word word)
    {
        if (Avx2.IsSupported)
        {
            Word shuffle = Vector256.Create(
                0x18191a1b1c1d1e1ful,
                0x1011121314151617ul,
                0x08090a0b0c0d0e0ful,
                0x0001020304050607ul).AsByte();
            if (Avx512Vbmi.VL.IsSupported)
            {
                word = Avx512Vbmi.VL.PermuteVar32x8(word, shuffle);
            }
            else
            {
                Word convert = Avx2.Shuffle(word, shuffle);
                Vector256<ulong> permute = Avx2.Permute4x64(Unsafe.As<Word, Vector256<ulong>>(ref convert), 0b_01_00_11_10);
                word = Unsafe.As<Vector256<ulong>, Word>(ref permute);
            }
        }
        else
        {
            throw new NotImplementedException();
        }
    }
}
