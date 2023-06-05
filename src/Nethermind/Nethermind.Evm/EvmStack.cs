// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm
{
    using Word = System.Runtime.Intrinsics.Vector256<byte>;

    public ref struct EvmStack
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
            Register = _bytes.Slice(MaxStackSize * WordSize, WordSize);
            Register.Clear();
        }

        public int Head;

        public Span<byte> Register;

        private Span<byte> _bytes;

        private ITxTracer _tracer;

        public void PushWord256(scoped in Span<byte> value)
        {
            if (value.Length != WordSize) ThrowArgumentOutOfRangeException();
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            int offset = Head * WordSize;
            IncrementStackPointer();

            // Direct 256bit register copy rather than invoke Memmove
            Unsafe.WriteUnaligned(
                ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), offset),
                Unsafe.As<byte, Word>(ref MemoryMarshal.GetReference(value))
            );
        }

        public void PushBytes(scoped in Span<byte> value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * WordSize, WordSize);
            if (value.Length != WordSize)
            {
                word.Clear();
                value.CopyTo(word.Slice(WordSize - value.Length, value.Length));
            }
            else
            {
                value.CopyTo(word);
            }

            IncrementStackPointer();
        }

        public void PushBytes(scoped in ZeroPaddedSpan value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            if (value.Span.Length != 32)
            {
                word.Clear();
                value.Span.CopyTo(word[..value.Span.Length]);
            }
            else
            {
                value.Span.CopyTo(word);
            }

            IncrementStackPointer();
        }

        public void PushBytes(scoped in ZeroPaddedMemory value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * WordSize, WordSize);
            if (value.Memory.Length != WordSize)
            {
                word.Clear();
                value.Memory.Span.CopyTo(word[..value.Memory.Length]);
            }
            else
            {
                value.Memory.Span.CopyTo(word);
            }

            IncrementStackPointer();
        }

        public void PushByte(byte value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * WordSize, WordSize);
            word.Clear();
            word[WordSize - sizeof(byte)] = value;

            IncrementStackPointer();
        }

        private static ReadOnlySpan<byte> OneStackItem => new byte[] { 1 };

        public void PushOne()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(OneStackItem);

            int start = Head * WordSize;
            Span<byte> word = _bytes.Slice(start, WordSize);
            word.Clear();
            word[WordSize - sizeof(byte)] = 1;

            IncrementStackPointer();
        }

        private static ReadOnlySpan<byte> ZeroStackItem => new byte[] { 0 };

        public void PushZero()
        {
            if (_tracer.IsTracingInstructions)
            {
                _tracer.ReportStackPush(ZeroStackItem);
            }

            _bytes.Slice(Head * WordSize, WordSize).Clear();

            IncrementStackPointer();
        }

        public void PushUInt32(int value)
        {
            Span<byte> word = _bytes.Slice(Head * WordSize, (WordSize - sizeof(int)));
            word.Clear();

            Span<byte> intPlace = _bytes.Slice(Head * WordSize + (WordSize - sizeof(int)), sizeof(int));
            BinaryPrimitives.WriteInt32BigEndian(intPlace, value);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            IncrementStackPointer();
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
            ref byte bytes = ref word[0];

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

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            IncrementStackPointer();
        }

        public void PushSignedInt256(in Int256.Int256 value)
        {
            // tail call into UInt256
            PushUInt256(Unsafe.As<Int256.Int256, UInt256>(ref Unsafe.AsRef(in value)));
        }

        public void PopLimbo()
        {
            DecrementStackPointer();
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

        public bool PeekUInt256IsZero()
        {
            int head = Head - 1;
            if (head <= 0)
            {
                return false;
            }

            ref byte bytes = ref _bytes[head * WordSize];
            return Unsafe.ReadUnaligned<UInt256>(ref bytes).IsZero;
        }

        public Address PopAddress()
        {
            DecrementStackPointer();

            return new Address(_bytes.Slice(Head * WordSize + (WordSize - AddressSize), AddressSize).ToArray());
        }

        private ref byte PopBytesByRef()
        {
            DecrementStackPointer();

            return ref _bytes[Head * WordSize];
        }

        public Word PopVector256()
        {
            DecrementStackPointer();

            return Unsafe.ReadUnaligned<Word>(ref Unsafe.Add(ref MemoryMarshal.GetReference(_bytes), Head * WordSize));
        }

        public Span<byte> PopWord256()
        {
            DecrementStackPointer();

            return _bytes.Slice(Head * WordSize, WordSize);
        }

        public byte PopByte()
        {
            DecrementStackPointer();

            return _bytes[Head * WordSize + (WordSize - sizeof(byte))];
        }

        public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            if (value.Length != WordSize)
            {
                _bytes.Slice(Head * WordSize, WordSize).Clear();
            }

            value.CopyTo(_bytes.Slice(Head * WordSize + WordSize - paddingLength, value.Length));

            IncrementStackPointer();
        }

        public void Dup(int depth)
        {
            EnsureDepth(depth);

            ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

            ref byte from = ref Unsafe.Add(ref bytes, (Head - depth) * WordSize);
            ref byte to = ref Unsafe.Add(ref bytes, Head * WordSize);

            Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));

            if (_tracer.IsTracingInstructions)
            {
                Trace(depth);
            }

            IncrementStackPointer();
        }

        public void EnsureDepth(int depth)
        {
            if (Head < depth)
            {
                ThrowEvmStackUnderflowException();
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

            if (_tracer.IsTracingInstructions)
            {
                Trace(depth);
            }
        }

        private void DecrementStackPointer()
        {
            if (Head-- == 0)
            {
                ThrowEvmStackUnderflowException();
            }
        }

        private void IncrementStackPointer()
        {
            if (++Head >= MaxStackSize)
            {
                ThrowEvmStackOverflowException();
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
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }

        [StackTraceHidden]
        [DoesNotReturn]
        private static void ThrowEvmStackUnderflowException()
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

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowArgumentOutOfRangeException()
        {
            Metrics.EvmExceptions++;
            throw new ArgumentOutOfRangeException("Word size must be 32 bytes");
        }
    }
}
