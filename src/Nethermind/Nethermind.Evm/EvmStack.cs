// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

using HalfWord = Vector128<byte>;

[StructLayout(LayoutKind.Auto)]
public ref partial struct EvmStack
{
    public const int RegisterLength = 1;
    public const int MaxStackSize = 1025;
    public const int WordSize = 32;
    public const int AddressSize = 20;

    public EvmStack(int head, ITxTracer txTracer, ref byte stack, scoped in ReadOnlySpan<byte> codeSpan, CodeInfo? codeInfo)
    {
        Head = head;
        _tracer = txTracer;
        _codeInfo = codeInfo;
        _stack = ref stack;
        Code = ref MemoryMarshal.GetReference(codeSpan);
        CodeLength = codeSpan.Length;
    }

    public EvmStack(int head, ref byte stack, scoped in ReadOnlySpan<byte> codeSpan, CodeInfo? codeInfo)
    {
        Head = head;
        _tracer = null!;
        _codeInfo = codeInfo;
        _stack = ref stack;
        Code = ref MemoryMarshal.GetReference(codeSpan);
        CodeLength = codeSpan.Length;
    }

    // Null only for stacks whose compile-time tracing flag eliminates every tracer read.
    private readonly ITxTracer _tracer;
    private readonly ref byte _stack;
    internal readonly ref byte Code;
    /// <summary>The index of the next free stack slot.</summary>
    /// <remarks>
    /// Native width for the same reason as <see cref="CodeLength"/>, and more so: this is read and
    /// written by every push and pop, and the zkEVM guest bills a 4-byte read-modify-write at roughly
    /// ten times an aligned one. It occupies padding the struct already had, so nothing grows.
    /// Every accessor keeps the index at this width. A cast to <see cref="uint"/> puts the narrow
    /// access back.
    /// </remarks>
    public nint Head;
    /// <summary>The length of <see cref="Code"/>.</summary>
    /// <remarks>
    /// Native width rather than <see cref="int"/>: the dispatch tests the program counter against it on
    /// every opcode, and the zkEVM guest bills a 4-byte read as an unaligned access, roughly eight times
    /// the cost of an aligned one. Every consumer already widens it to <see cref="nint"/> to combine it
    /// with a program counter.
    /// </remarks>
    internal readonly nint CodeLength;
    private readonly CodeInfo? _codeInfo;
    private long[]? _jumpDestinations;

    /// <summary>The jump-destination bitmap of <see cref="Code"/>, resolved on the first in-range jump and kept in the frame.</summary>
    /// <remarks>
    /// Kept on the stack so a jump validates against the frame it is executing without walking
    /// <c>vm.VmState.Env.CodeInfo</c>. Resolving lazily keeps the analysis off the path of frames that
    /// never jump. Only a stack built over no code may omit the code info; the empty bitmap then rejects
    /// every destination.
    /// </remarks>
    internal long[] JumpDestinations
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            Debug.Assert(_codeInfo is not null || CodeLength == 0, "A stack that executes code must carry that code's CodeInfo.");
            return _jumpDestinations ??= _codeInfo?.JumpDestinationBitmap ?? JumpDestinationAnalyzer.EmptyBitmap;
        }
    }

    /// <summary>
    /// Reserves the next stack slot and returns a ref to it. On overflow returns <see cref="Unsafe.NullRef{T}"/>;
    /// callers must check with <see cref="Unsafe.IsNullRef{T}"/> before writing.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PushBytesRef()
    {
        // Workhorse method
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return ref Unsafe.NullRef<byte>();
        }

        Head = newOffset;
        return ref Unsafe.Add(ref _stack, headOffset * WordSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmWord CreateAcceleratedWordFromUInt64(ulong value)
        => Vector256.Create(value, 0UL, 0UL, 0UL).AsByte();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteScalarWordFromUInt64(ref EvmWord word, ulong value)
    {
        ref ulong parts = ref Unsafe.As<EvmWord, ulong>(ref word);
        parts = value;
        Unsafe.Add(ref parts, 1) = 0;
        Unsafe.Add(ref parts, 2) = 0;
        Unsafe.Add(ref parts, 3) = 0;
    }

    /// <summary>
    /// Writes the <paramref name="length"/> big-endian bytes at <paramref name="value"/> into
    /// <paramref name="word"/>, zero-extended, in the slot's limb layout.
    /// </summary>
    /// <remarks>
    /// Each limb is built in a register and the word stored once. Writing the big-endian bytes first
    /// and reversing the slot afterwards costs a reload that overlaps the stores which just wrote it.
    /// <para>
    /// A limb the value covers is one reversed 8-byte load. The single limb it covers only partly
    /// reads the first 8 bytes - in range for every <paramref name="length"/> here - and keeps the
    /// leading <c>length % 8</c> of them. <paramref name="length"/> is a literal at every call site,
    /// so the tests and shifts below fold away.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteWordFromBigEndianBytes(ref EvmWord word, ref byte value, int length)
    {
        Debug.Assert(length is > sizeof(ulong) and <= WordSize, "Shorter values reach the stack through one limb.");
        const int limbBytes = sizeof(ulong);
        int fullLimbs = length / limbBytes;
        int partialBytes = length % limbBytes;
        ulong partial = partialBytes == 0
            ? 0
            : Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref value)) >> ((limbBytes - partialBytes) * 8);

        ulong limb0 = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, length - limbBytes)));
        ulong limb1 = fullLimbs > 1
            ? Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, length - 2 * limbBytes)))
            : partial;
        ulong limb2 = fullLimbs > 2
            ? Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref value, length - 3 * limbBytes)))
            : fullLimbs == 2 ? partial : 0;
        ulong limb3 = fullLimbs > 3
            ? Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref value))
            : fullLimbs == 3 ? partial : 0;

        if (Vector256.IsHardwareAccelerated)
        {
            // A full-width value is one load and one shuffle; the shorter ones assemble their limbs.
            word = length == WordSize
                ? Unsafe.ReadUnaligned<EvmWord>(ref value).ByteSwap()
                : Vector256.Create(limb0, limb1, limb2, limb3).AsByte();
        }
        else
        {
            ref ulong limbs = ref Unsafe.As<EvmWord, ulong>(ref word);
            limbs = limb0;
            Unsafe.Add(ref limbs, 1) = limb1;
            Unsafe.Add(ref limbs, 2) = limb2;
            Unsafe.Add(ref limbs, 3) = limb3;
        }
    }

    /// <summary>
    /// Turns the big-endian bytes a producer wrote into a slot into the word's limb layout, or back.
    /// A slot holds the <see cref="UInt256"/> memory layout — least significant limb first — so
    /// arithmetic reads and writes it as is, and only the byte-oriented boundaries (code immediates,
    /// memory, storage values, hashes, addresses) pay for the reversal.
    /// </summary>
    /// <remarks>
    /// The guest reverses the limbs itself instead of routing a <see cref="EvmWord"/> through
    /// <c>ByteSwap</c>: that form takes the address of both the argument and the result, staging the
    /// word on the frame twice, and re-forms the swap masks for each of the four lanes. Reading every
    /// limb before writing any is what lets the swap run over the slot in place.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SwapSlot(ref byte slot)
    {
        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.WriteUnaligned(ref slot, Unsafe.ReadUnaligned<EvmWord>(ref slot).ByteSwap());
            return;
        }

        ref ulong limbs = ref Unsafe.As<byte, ulong>(ref slot);
        ulong limb0 = limbs;
        ulong limb1 = Unsafe.Add(ref limbs, 1);
        ulong limb2 = Unsafe.Add(ref limbs, 2);
        ulong limb3 = Unsafe.Add(ref limbs, 3);
        Bytes.Bswap64Hoist swap = Bytes.HoistBswap64();
        limbs = swap.Bswap64(limb3);
        Unsafe.Add(ref limbs, 1) = swap.Bswap64(limb2);
        Unsafe.Add(ref limbs, 2) = swap.Bswap64(limb1);
        Unsafe.Add(ref limbs, 3) = swap.Bswap64(limb0);
    }

    /// <summary>
    /// Writes the slot at <paramref name="slot"/> to <paramref name="word"/> reversed into big-endian
    /// bytes, which is how tracers and their converters read a stack.
    /// </summary>
    public static void WriteBigEndianWord(ReadOnlySpan<byte> slot, Span<byte> word)
    {
        Debug.Assert(slot.Length >= WordSize && word.Length >= WordSize, "Both sides hold a whole word.");
        Unsafe.WriteUnaligned(
            ref MemoryMarshal.GetReference(word),
            Unsafe.ReadUnaligned<EvmWord>(ref MemoryMarshal.GetReference(slot)).ByteSwap());
    }

    /// <summary><inheritdoc cref="WriteBigEndianWord" path="/summary"/> for every whole slot in <paramref name="slots"/>.</summary>
    public static void WriteBigEndianWords(ReadOnlySpan<byte> slots, Span<byte> words)
    {
        for (int offset = 0; offset + WordSize <= slots.Length; offset += WordSize)
        {
            WriteBigEndianWord(slots.Slice(offset), words.Slice(offset));
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
                Unsafe.As<byte, EvmWord>(ref dst) = Unsafe.As<byte, EvmWord>(ref src);
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
        SwapSlot(ref dst);
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
            Unsafe.As<byte, EvmWord>(ref dst) = Vector256.Create(lo, hi).AsByte();
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

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushRightPaddedBytes<TTracingInst>(ref byte src, uint length)
        where TTracingInst : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        ref byte dst = ref Unsafe.Add(ref _stack, headOffset * WordSize);

        return WriteRightPaddedBytes<TTracingInst>(ref dst, ref src, length);
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EvmExceptionType WriteRightPaddedBytes<TTracingInst>(ref byte dst, ref byte src, uint length)
        where TTracingInst : struct, IFlag
    {
        if (length != WordSize)
        {
            return PushBytesPartialZeroPadded<TTracingInst>(ref dst, ref src, length);
        }

        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.As<byte, EvmWord>(ref dst) = Unsafe.ReadUnaligned<EvmWord>(ref src);
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = Unsafe.ReadUnaligned<HalfWord>(ref src);
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) =
                Unsafe.ReadUnaligned<HalfWord>(ref Unsafe.Add(ref src, 16));
        }
        SwapSlot(ref dst);
        if (TTracingInst.IsActive)
            ReportPushWord(ref dst);
        return EvmExceptionType.None;
    }

    [SkipLocalsInit]
    private EvmExceptionType PushBytesPartialZeroPadded<TTracingInst>(ref byte dst, ref byte src, nuint length)
        where TTracingInst : struct, IFlag
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
            Unsafe.As<byte, EvmWord>(ref dst) = Vector256.Create(lo, hi).AsByte();
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = lo.AsByte();
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) = hi.AsByte();
        }
        SwapSlot(ref dst);
        if (TTracingInst.IsActive)
            ReportPushWord(ref dst);
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

    /// <summary>
    /// Reports the raw 32-byte word at the given stack slot to the tracer as a stack push
    /// (also used to trace in-place top-of-stack updates).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public readonly void ReportPushWord(ref byte slot)
    {
        EvmWord bigEndian = Unsafe.ReadUnaligned<EvmWord>(ref slot).ByteSwap();
        _tracer.ReportStackPush(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.As<EvmWord, byte>(ref bigEndian), WordSize));
    }

    /// <summary>
    /// Reads a UInt256 value from a stack slot, which holds the UInt256 limb layout (no bounds check).
    /// Used when the slot was already validated by a previous operation.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static UInt256 ReadUInt256FromSlot(ref byte slot)
        => Unsafe.ReadUnaligned<UInt256>(ref slot);

    /// <summary>
    /// Out-parameter form of <see cref="ReadUInt256FromSlot(ref byte)"/>. Writes directly
    /// into <paramref name="value"/>, bypassing the 32-byte return-value staging buffer
    /// the JIT otherwise emits for a by-value UInt256 return.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ReadUInt256FromSlot(ref byte slot, out UInt256 value)
        => value = Unsafe.ReadUnaligned<UInt256>(ref slot);

    /// <summary>Reads a stack slot as a memory position.</summary>
    /// <remarks>
    /// A position above <see cref="ulong.MaxValue"/> is unreachable, so every consumer reads a
    /// position as <c>IsUint64</c> plus <c>u0</c> and rejects the access when the first is false.
    /// Folding the three high limbs into a single non-zero marker keeps both of those exact while
    /// reading one limb instead of the whole word. The word then never has to be written to
    /// the frame as a vector and read straight back as scalars, which does not forward.
    /// </remarks>
    /// <param name="slot">The stack slot, in limb layout.</param>
    /// <param name="position">The decoded position.</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ReadMemoryPositionFromSlot(ref byte slot, out UInt256 position)
    {
        ref ulong limbs = ref Unsafe.As<byte, ulong>(ref slot);
        ulong unreachable = Unsafe.Add(ref limbs, 1) | Unsafe.Add(ref limbs, 2) | Unsafe.Add(ref limbs, 3);
        position = new UInt256(limbs, 0, 0, unreachable);
    }

    /// <summary>Pops a memory position.</summary>
    /// <remarks>See <see cref="ReadMemoryPositionFromSlot"/> for what the popped value preserves.</remarks>
    /// <param name="position">The popped position.</param>
    /// <returns><see langword="false"/> on stack underflow.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopMemoryPosition(out UInt256 position)
    {
        Unsafe.SkipInit(out position);
        nint head = Head - 1;
        if (head < 0)
        {
            return false;
        }
        Head = head;
        ReadMemoryPositionFromSlot(ref Unsafe.Add(ref _stack, head * WordSize), out position);
        return true;
    }

    /// <summary>
    /// Writes a UInt256 value to a stack slot in its limb layout (no bounds check).
    /// Used when the slot was already validated by a previous pop operation.
    /// </summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteUInt256ToSlot(ref byte slot, in UInt256 value)
        => Unsafe.WriteUnaligned(ref slot, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push10Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 10);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 10);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push11Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 11);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 11);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push12Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 12);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 12);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push13Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 13);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 13);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push14Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 14);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 14);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push15Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 15);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 15);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push16Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 16);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 16);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push17Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 17);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 17);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push18Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 18);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 18);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push19Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 19);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 19);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push20Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 20);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 20);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push21Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 21);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 21);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push22Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 22);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 22);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push23Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 23);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 23);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push24Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 24);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 24);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push25Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 25);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 25);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push26Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 26);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 26);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push27Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 27);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 27);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push28Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 28);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 28);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push29Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 29);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 29);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push2Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 2);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        // Build the full 32-byte value in a register and emit a single vector store;
        // zero-then-overwrite would be two stores.
        ulong word = Bytes.Bswap64((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 48);
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push30Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 30);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 30);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push31Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 31);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 31);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push32Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 32);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 32);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push3Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 3);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        ulong word =
            ((ulong)Unsafe.ReadUnaligned<ushort>(ref value) << 40) |
            ((ulong)Unsafe.Add(ref value, 2) << 56);
        word = Bytes.Bswap64(word); // the immediate's bytes, read as a lane, reversed into the value
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push4Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 4);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        ulong word = Bytes.Bswap64((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 32);
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push5Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 5);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        ulong word =
            ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 24) |
            ((ulong)Unsafe.Add(ref value, 4) << 56);
        word = Bytes.Bswap64(word); // the immediate's bytes, read as a lane, reversed into the value
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push6Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 6);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        ulong word =
            ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 16) |
            ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 48);
        word = Bytes.Bswap64(word); // the immediate's bytes, read as a lane, reversed into the value
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push7Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 7);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        ulong word =
            ((ulong)Unsafe.ReadUnaligned<uint>(ref value) << 8) |
            ((ulong)Unsafe.ReadUnaligned<ushort>(ref Unsafe.Add(ref value, 4)) << 40) |
            ((ulong)Unsafe.Add(ref value, 6) << 56);
        word = Bytes.Bswap64(word); // the immediate's bytes, read as a lane, reversed into the value
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push8Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 8);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        ulong word = Bytes.Bswap64(Unsafe.ReadUnaligned<ulong>(ref value));
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push9Bytes<TTracingInst, TCheckDepth>(ref byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.TraceBytes(in value, 9);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        WriteWordFromBigEndianBytes(ref head, ref value, 9);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushByte<TTracingInst, TCheckDepth>(byte value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            _tracer.ReportStackPush(value);
        }

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(value);
        else
            WriteScalarWordFromUInt64(ref head, value);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushAddress<TTracingInst>(Address address)
        where TTracingInst : struct, IFlag
        => Push20Bytes<TTracingInst, OnFlag>(ref MemoryMarshal.GetReference(address.Bytes));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Push32Bytes<TTracingInst, TCheckDepth>(in ValueHash256 hash)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
        => Push32Bytes<TTracingInst, TCheckDepth>(ref Unsafe.As<ValueHash256, byte>(ref Unsafe.AsRef(in hash)));

    /// <summary>
    /// Fallback writer for truncated PUSH{n} where fewer than <paramref name="pushSize"/> immediate
    /// bytes remain in code. Zero-fills the 32-byte word, copies <paramref name="used"/> bytes
    /// to the leading portion of the n-byte PUSH immediate, then reverses the word into limb layout.
    /// </summary>
    /// <param name="start">Reference to the first immediate byte in code.</param>
    /// <param name="used">Number of immediate bytes available in code (0 <= used <= pushSize).</param>
    /// <param name="pushSize">The PUSH opcode's declared immediate length (2..32).</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public EvmExceptionType PushBothPaddedBytes<TTracingInst, TCheckDepth>(ref byte start, int used, int pushSize)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        ref byte dst = ref Unsafe.Add(ref _stack, headOffset * WordSize);

        // Truncated PUSH32 is just a right-padded partial write, so reuse the tighter helper.
        if (pushSize == WordSize)
        {
            return PushBytesPartialZeroPadded<TTracingInst>(ref dst, ref start, (uint)used);
        }

        // Zeros on both sides.
        if (Vector256.IsHardwareAccelerated)
        {
            Unsafe.As<byte, EvmWord>(ref dst) = default;
        }
        else
        {
            Unsafe.As<byte, HalfWord>(ref dst) = default;
            Unsafe.As<byte, HalfWord>(ref Unsafe.Add(ref dst, 16)) = default;
        }

        // When no immediate bytes are available (truncated PUSH at end of code), the
        // zero-filled word is already correct.
        if (used != 0)
        {
            CopyUpTo32(ref Unsafe.Add(ref dst, WordSize - pushSize), ref start, (uint)used);
            SwapSlot(ref dst);
        }
        if (TTracingInst.IsActive) ReportPushWord(ref dst);
        return EvmExceptionType.None;
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
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.OneByteSpan);

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(1UL);
        else
            WriteScalarWordFromUInt64(ref head, 1UL);
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [SkipLocalsInit]
    public EvmExceptionType PushZero<TTracingInst, TCheckDepth>()
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
            _tracer.ReportStackPush(Bytes.ZeroByteSpan);

        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));

        if (Vector256.IsHardwareAccelerated)
        {
            // Single 32-byte store
            head = default;
        }
        else
        {
            ref Vector128<uint> head128 = ref Unsafe.As<EvmWord, Vector128<uint>>(ref head);
            head128 = default;
            Unsafe.Add(ref head128, 1) = default;
        }
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushUInt32<TTracingInst, TCheckDepth>(uint value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            uint bigEndian = BinaryPrimitives.ReverseEndianness(value);
            _tracer.TraceBytes(in Unsafe.As<uint, byte>(ref bigEndian), sizeof(uint));
        }
        ulong word = value;
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(word);
        else
            WriteScalarWordFromUInt64(ref head, word);
        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushUInt64<TTracingInst, TCheckDepth>(ulong value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        if (TTracingInst.IsActive)
        {
            ulong bigEndian = Bytes.Bswap64(value);
            _tracer.TraceBytes(in Unsafe.As<ulong, byte>(ref bigEndian), sizeof(ulong));
        }
        if (Vector128.IsHardwareAccelerated)
            head = CreateAcceleratedWordFromUInt64(value);
        else
            WriteScalarWordFromUInt64(ref head, value);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Pushes an UInt256.
    /// </summary>
    /// <remarks>
    /// This method is a counterpart to <see cref="PopUInt256"/> and uses the same, raw data approach to write data back.
    /// </remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType PushUInt256<TTracingInst>(in UInt256 value)
        where TTracingInst : struct, IFlag
        => PushUInt256<TTracingInst, OnFlag>(in value);

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal EvmExceptionType PushUInt256<TTracingInst, TCheckDepth>(in UInt256 value)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint headOffset = Head;
        nint newOffset = headOffset + 1;
        ref EvmWord head = ref Unsafe.As<byte, EvmWord>(ref Unsafe.Add(ref _stack, headOffset * WordSize));
        if (TCheckDepth.IsActive && newOffset >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }
        Head = newOffset;

        WriteUInt256ToSlot(ref Unsafe.As<EvmWord, byte>(ref head), in value);
        if (TTracingInst.IsActive)
            ReportPushWord(ref Unsafe.As<EvmWord, byte>(ref head));

        return EvmExceptionType.None;
    }

    public EvmExceptionType PushSignedInt256<TTracingInst>(in Int256.Int256 value)
        where TTracingInst : struct, IFlag
        => PushUInt256<TTracingInst>(in Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopLimbo()
    {
        nint head = Head - 1;
        if (head < 0)
        {
            return false;
        }
        Head = head;
        return true;
    }

    /// <summary>
    /// Pops an UInt256.
    /// </summary>
    /// <remarks>
    /// This method does its own calculations to create the <paramref name="result"/>. It knows that 32 bytes were popped with <see cref="PopBytesByRef"/>. It doesn't have to check the size of span or slice it.
    /// All it does is <see cref="Unsafe.ReadUnaligned{T}(ref byte)"/>, as the slot already holds the limb layout of <paramref name="result"/>.
    /// </remarks>
    /// <param name="result">The returned value.</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopUInt256(out UInt256 result)
    {
        Unsafe.SkipInit(out result);
        ref byte baseRef = ref _stack;
        nint head = Head - 1;
        if (head < 0)
        {
            return false;
        }
        Head = head;
        ref byte bytes = ref Unsafe.Add(ref baseRef, head * WordSize);

        ReadUInt256FromSlot(ref bytes, out result);

        return true;
    }

    /// <summary>
    /// Pops two UInt256 values, amortising bounds checking
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

        nint head = Head;
        nint newHead = head - 2;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;

        ref byte bytes = ref Unsafe.Add(ref _stack, newHead * WordSize);
        // Memory layout: [b @ +0] [a @ +32]

        b = Unsafe.ReadUnaligned<UInt256>(ref bytes);
        a = Unsafe.ReadUnaligned<UInt256>(ref Unsafe.Add(ref bytes, WordSize));
        return true;
    }

    /// <summary>
    /// Pops three UInt256 values, amortising bounds checking
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

        nint head = Head;
        nint newHead = head - 3;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;

        ref byte bytes = ref Unsafe.Add(ref _stack, newHead * WordSize);
        // Memory layout: [c @ +0] [b @ +32] [a @ +64]

        c = Unsafe.ReadUnaligned<UInt256>(ref bytes);
        b = Unsafe.ReadUnaligned<UInt256>(ref Unsafe.Add(ref bytes, WordSize));
        a = Unsafe.ReadUnaligned<UInt256>(ref Unsafe.Add(ref bytes, 2 * WordSize));
        return true;
    }

    /// <summary>
    /// Pops four UInt256 values, amortising bounds checking
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

        nint head = Head;
        nint newHead = head - 4;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;

        ref byte bytes = ref Unsafe.Add(ref _stack, newHead * WordSize);
        // Memory layout: [d @ +0] [c @ +32] [b @ +64] [a @ +96]

        d = Unsafe.ReadUnaligned<UInt256>(ref bytes);
        c = Unsafe.ReadUnaligned<UInt256>(ref Unsafe.Add(ref bytes, WordSize));
        b = Unsafe.ReadUnaligned<UInt256>(ref Unsafe.Add(ref bytes, 2 * WordSize));
        a = Unsafe.ReadUnaligned<UInt256>(ref Unsafe.Add(ref bytes, 3 * WordSize));
        return true;
    }

    public readonly bool PeekUInt256IsZero()
    {
        ref byte baseRef = ref _stack;
        nint head = Head - 1;
        if (head < 0)
        {
            return false;
        }

        return IsSlotZero(ref Unsafe.Add(ref baseRef, head * WordSize));
    }

    /// <summary>
    /// The top slot, for callers that have already established <c>Head &gt;= 1</c> with
    /// <see cref="EnsureDepth"/>.
    /// </summary>
    /// <remarks>Same reasoning as <see cref="Pop1Peek32BytesUnchecked()"/>.</remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly ref byte PeekBytesByRefUnchecked()
    {
        Debug.Assert(Head >= 1, "Caller must establish the depth before peeking unchecked");
        return ref Unsafe.Add(ref _stack, (nint)(((nuint)Head - 1) * WordSize));
    }


    public Address? PopAddress()
    {
        nint head = Head - 1;
        if (head < 0) return null;
        Head = head;
        SwapSlot(ref Unsafe.Add(ref _stack, head * WordSize));
        return new Address(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _stack, head * WordSize + WordSize - AddressSize), AddressSize));
    }

    /// <summary>
    /// Pops an address, reusing the cached instance when the popped bytes match the previously popped address.
    /// </summary>
    [SkipLocalsInit]
    public Address? PopAddress(PoppedAddressCache cache)
    {
        nint head = Head - 1;
        if (head < 0) return null;
        Head = head;
        SwapSlot(ref Unsafe.Add(ref _stack, head * WordSize));
        return cache.GetOrCreate(MemoryMarshal.CreateReadOnlySpan(ref Unsafe.Add(ref _stack, head * WordSize + WordSize - AddressSize), AddressSize));
    }

    public bool PopAddress([NotNullWhen(true)] out Address? address)
    {
        nint head = Head - 1;
        if (head < 0)
        {
            address = null;
            return false;
        }
        Head = head;
        SwapSlot(ref Unsafe.Add(ref _stack, head * WordSize));
        address = new Address(MemoryMarshal.CreateSpan(ref Unsafe.Add(ref _stack, head * WordSize + WordSize - AddressSize), AddressSize));
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref byte PopBytesByRef()
    {
        ref byte baseRef = ref _stack;
        nint head = Head;
        if (head == 0)
        {
            return ref Unsafe.NullRef<byte>();
        }
        Head = --head;
        return ref Unsafe.Add(ref baseRef, head * WordSize);
    }

    /// <summary>
    /// Pops one word for callers that have already established <c>Head &gt;= 1</c> with
    /// <see cref="EnsureDepth"/>.
    /// </summary>
    /// <remarks>Same reasoning as <see cref="Pop1Peek32BytesUnchecked()"/>.</remarks>
    /// <returns>Reference to the popped slot, which stays readable until the next push.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    internal ref byte PopBytesByRefUnchecked()
    {
        Debug.Assert(Head >= 1, "Caller must establish the depth before popping unchecked");
        nuint head = (nuint)Head - 1;
        Head = (nint)head;
        return ref Unsafe.Add(ref _stack, (nint)(head * WordSize));
    }

    /// <summary>
    /// Pops two words for callers that have already established <c>Head &gt;= 2</c> with
    /// <see cref="EnsureDepth"/>.
    /// </summary>
    /// <remarks>Same reasoning as <see cref="Pop1Peek32BytesUnchecked()"/>.</remarks>
    /// <returns>Reference to the second popped slot; the first sits one word above it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    internal ref byte Pop2BytesByRefUnchecked()
    {
        Debug.Assert(Head >= 2, "Caller must establish the depth before popping unchecked");
        nuint head = (nuint)Head - 2;
        Head = (nint)head;
        return ref Unsafe.Add(ref _stack, (nint)(head * WordSize));
    }

    /// <summary>Whether a stack slot holds zero.</summary>
    /// <remarks>
    /// Folding a whole word down to one bit is the case where a <see cref="EvmWord"/> value has to be
    /// address-taken on targets that cannot hold one in a register, which spills the slot to the frame
    /// and reads it back. Both widths test the slot where it lies instead.
    /// <para>
    /// There is deliberately no 128-bit middle path. Reducing a pair of <see cref="HalfWord"/>s to a
    /// bool costs a cross-domain move that the limbs do not: on SSE it measured ISZERO at 4.856 ns
    /// against 3.124 ns for the limbs, and on ARM64 it is the <c>umov</c> this branch removes
    /// elsewhere.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsSlotZero(ref byte slot)
    {
        if (Vector256.IsHardwareAccelerated)
            return Unsafe.ReadUnaligned<EvmWord>(ref slot) == default;

        ref ulong parts = ref Unsafe.As<byte, ulong>(ref slot);
        return (parts | Unsafe.Add(ref parts, 1) | Unsafe.Add(ref parts, 2) | Unsafe.Add(ref parts, 3)) == 0UL;
    }

    /// <summary>
    /// Pop-1 + peek-top for callers that have already established <c>Head &gt;= 2</c> with
    /// <see cref="EnsureDepth"/>.
    /// </summary>
    /// <remarks>
    /// A helper that reports the depth through a flag has to merge a success and a failure path before
    /// it returns, so the caller branches once on the depth and again on the flag. Checking the depth in
    /// the caller lets it return straight from the failing compare, which is then the only branch on the
    /// path, and lets the head reach the address arithmetic as a single native-width read.
    /// </remarks>
    /// <returns>Reference to the new top slot; the popped word sits one word above it.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    internal ref byte Pop1Peek32BytesUnchecked()
    {
        Debug.Assert(Head >= 2, "Caller must establish the depth before popping unchecked");
        nuint head = (nuint)Head;
        Head = (nint)(head - 1);
        return ref Unsafe.Add(ref _stack, (nint)((head - 2) * WordSize));
    }

    /// <inheritdoc cref="Pop1Peek32BytesUnchecked()"/>
    /// <param name="a">The popped value, decoded from the slot above the returned one.</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    internal ref byte Pop1Peek32BytesUnchecked(out UInt256 a)
    {
        ref byte topRef = ref Pop1Peek32BytesUnchecked();
        ReadUInt256FromSlot(ref Unsafe.Add(ref topRef, WordSize), out a);
        return ref topRef;
    }

    /// <summary>
    /// Pop-2 + peek-top for callers that have already established <c>Head &gt;= 3</c> with
    /// <see cref="EnsureDepth"/>.
    /// </summary>
    /// <remarks>Same reasoning as <see cref="Pop1Peek32BytesUnchecked()"/>.</remarks>
    /// <returns>Reference to the new top slot.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    [UnscopedRef]
    internal ref byte Pop2Peek32BytesUnchecked()
    {
        Debug.Assert(Head >= 3, "Caller must establish the depth before popping unchecked");
        nuint head = (nuint)Head;
        Head = (nint)(head - 2);
        return ref Unsafe.Add(ref _stack, (nint)((head - 3) * WordSize));
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
        SwapSlot(ref bytes);
        return MemoryMarshal.CreateSpan(ref bytes, WordSize);
    }

    /// <summary>
    /// Atomic pop of a memory position + a raw 32-byte word with a single bounds check.
    /// Callers such as MSTORE pop both in sequence; amortising avoids a redundant
    /// underflow check and resolves the mismatched throw/try-pattern on the two reads.
    /// </summary>
    /// <param name="position">The top-of-stack value decoded by <see cref="ReadMemoryPositionFromSlot"/>.</param>
    /// <param name="word">A span over the second slot, its 32 bytes reversed into big-endian order.</param>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopMemoryPositionAndWord256(out UInt256 position, out Span<byte> word)
    {
        Unsafe.SkipInit(out position);
        nint newHead = Head - 2;
        if (newHead < 0)
        {
            word = default;
            return false;
        }
        Head = newHead;
        ref byte baseRef = ref _stack;
        ReadMemoryPositionFromSlot(ref Unsafe.Add(ref baseRef, (newHead + 1) * WordSize), out position);
        ref byte slot = ref Unsafe.Add(ref baseRef, newHead * WordSize);
        SwapSlot(ref slot);
        word = MemoryMarshal.CreateSpan(ref slot, WordSize);
        return true;
    }

    /// <summary>Pops a memory position and the value beneath it.</summary>
    /// <remarks>
    /// Only the position is folded; the value beneath it keeps every limb because callers measure and
    /// compare it. See <see cref="ReadMemoryPositionFromSlot"/> for what the fold preserves.
    /// </remarks>
    /// <param name="position">The popped position (was at top of stack).</param>
    /// <param name="value">The popped value (was deeper).</param>
    /// <returns><see langword="false"/> on stack underflow.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopMemoryPositionAndUInt256(out UInt256 position, out UInt256 value)
    {
        Unsafe.SkipInit(out position);
        Unsafe.SkipInit(out value);
        nint newHead = Head - 2;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;
        ref byte baseRef = ref Unsafe.Add(ref _stack, newHead * WordSize);
        ReadUInt256FromSlot(ref baseRef, out value);
        ReadMemoryPositionFromSlot(ref Unsafe.Add(ref baseRef, WordSize), out position);
        return true;
    }

    /// <summary>Pops a memory position and the two values beneath it.</summary>
    /// <remarks>
    /// Only the position is folded; a source offset and a length beneath it keep every limb because
    /// callers add and compare them. See <see cref="ReadMemoryPositionFromSlot"/>.
    /// </remarks>
    /// <param name="position">The popped position (was at top of stack).</param>
    /// <param name="b">The second popped value.</param>
    /// <param name="c">The third popped value (was deepest).</param>
    /// <returns><see langword="false"/> on stack underflow.</returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PopMemoryPositionAndUInt256(out UInt256 position, out UInt256 b, out UInt256 c)
    {
        Unsafe.SkipInit(out position);
        Unsafe.SkipInit(out b);
        Unsafe.SkipInit(out c);
        nint newHead = Head - 3;
        if (newHead < 0)
        {
            return false;
        }
        Head = newHead;
        ref byte baseRef = ref Unsafe.Add(ref _stack, newHead * WordSize);
        ReadUInt256FromSlot(ref baseRef, out c);
        ReadUInt256FromSlot(ref Unsafe.Add(ref baseRef, WordSize), out b);
        ReadMemoryPositionFromSlot(ref Unsafe.Add(ref baseRef, 2 * WordSize), out position);
        return true;
    }

    [SkipLocalsInit]
    public bool PopWord256(out Span<byte> word)
    {
        nint head = Head - 1;
        if (head < 0)
        {
            word = default;
            return false;
        }
        Head = head;
        ref byte slot = ref Unsafe.Add(ref _stack, head * WordSize);
        SwapSlot(ref slot);
        word = MemoryMarshal.CreateSpan(ref slot, WordSize);
        return true;
    }

    public int PopByte()
    {
        nint head = Head;
        if (head == 0) goto Underflow;

        Head = head - 1;
        return Unsafe.Add(ref _stack, (head - 1) << 5);

    Underflow:
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryPopSmallIndex(out uint value)
    {
        nint head = Head;
        if (head == 0)
        {
            value = 0;
            return false;
        }
        Head = head - 1;
        ref ulong limbs = ref Unsafe.As<byte, ulong>(ref Unsafe.Add(ref _stack, (head - 1) << 5));
        if ((Unsafe.Add(ref limbs, 1) | Unsafe.Add(ref limbs, 2) | Unsafe.Add(ref limbs, 3)) != 0)
        {
            value = uint.MaxValue; // Signals a >= 32
            return true;
        }
        ulong low = limbs;
        value = low <= uint.MaxValue ? (uint)low : uint.MaxValue;
        return true;
    }

    /// <remarks>When <typeparamref name="TCheckDepth"/> is inactive, the caller must verify <paramref name="depth"/> items and room for one more.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public EvmExceptionType Dup<TTracingInst, TCheckDepth>(int depth)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint head = Head;
        if (TCheckDepth.IsActive && head < depth)
        {
            return EvmExceptionType.StackUnderflow;
        }

        ref byte bytes = ref _stack;
        // Use nuint to eliminate sign extension; parallel shifts
        nuint headOffset = (nuint)head << 5;
        nuint depthBytes = (nuint)(uint)depth << 5;

        ref byte to = ref Unsafe.Add(ref bytes, headOffset);
        ref byte from = ref Unsafe.Add(ref bytes, headOffset - depthBytes);

        head++;
        if (TCheckDepth.IsActive && head >= MaxStackSize)
        {
            return EvmExceptionType.StackOverflow;
        }

        if (TTracingInst.IsActive) Trace(depth);

        Head = head;
        Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<EvmWord>(ref from));
        return EvmExceptionType.None;
    }

    public readonly bool EnsureDepth(int depth)
        => Head >= depth;

    /// <remarks>When <typeparamref name="TCheckDepth"/> is inactive, the caller must have verified at least <paramref name="depth"/> stack items.</remarks>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly EvmExceptionType Swap<TTracingInst, TCheckDepth>(int depth)
        where TTracingInst : struct, IFlag
        where TCheckDepth : struct, IFlag
    {
        nint head = Head;
        if (TCheckDepth.IsActive && head < depth)
        {
            return EvmExceptionType.StackUnderflow;
        }

        ref byte bytes = ref _stack;

        nuint headOffset = (nuint)head << 5;
        nuint depthBytes = (nuint)(uint)depth << 5;

        ref byte bottom = ref Unsafe.Add(ref bytes, headOffset - depthBytes);
        ref byte top = ref Unsafe.Add(ref bytes, headOffset - WordSize);

        EvmWord buffer = Unsafe.ReadUnaligned<EvmWord>(ref bottom);
        Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<EvmWord>(ref top));
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

        nuint headOffset = (nuint)Head * WordSize;
        ref byte first = ref Unsafe.Add(ref bytes, headOffset - (nuint)(uint)n * WordSize);
        ref byte second = ref Unsafe.Add(ref bytes, headOffset - (nuint)(uint)m * WordSize);

        EvmWord buffer = Unsafe.ReadUnaligned<EvmWord>(ref first);
        Unsafe.WriteUnaligned(ref first, Unsafe.ReadUnaligned<EvmWord>(ref second));
        Unsafe.WriteUnaligned(ref second, buffer);

        if (TTracingInst.IsActive) Trace(maxDepth);

        return EvmExceptionType.None;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private readonly void Trace(int depth)
    {
        for (int i = depth; i > 0; i--)
        {
            ReportPushWord(ref Unsafe.Add(ref _stack, Head * WordSize - i * WordSize));
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
