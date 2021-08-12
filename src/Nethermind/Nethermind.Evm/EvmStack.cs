//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm
{
    public ref struct EvmStack
    {
        public const int RegisterLength = 1;
        public const int MaxStackSize = 1025;
        public const int ReturnStackSize = 1023;

        public EvmStack(in Span<byte> bytes, in int head, ITxTracer txTracer)
        {
            _bytes = bytes;
            Head = head;
            _tracer = txTracer;
            Register = _bytes.Slice(MaxStackSize * 32, 32);
            Register.Clear();
        }

        public int Head;

        public Span<byte> Register;

        private Span<byte> _bytes;

        private ITxTracer _tracer;

        public void PushBytes(in Span<byte> value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            if (value.Length != 32)
            {
                word.Clear();
                value.CopyTo(word.Slice(32 - value.Length, value.Length));
            }
            else
            {
                value.CopyTo(word);
            }

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushBytes(in ZeroPaddedSpan value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            if (value.Span.Length != 32)
            {
                word.Clear();
                value.Span.CopyTo(word.Slice(0, value.Span.Length));
            }
            else
            {
                value.Span.CopyTo(word);
            }

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }
        
        public void PushBytes(in ZeroPaddedMemory value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            if (value.Memory.Length != 32)
            {
                word.Clear();
                value.Memory.Span.CopyTo( word.Slice(0, value.Memory.Length));
            }
            else
            {
                value.Memory.Span.CopyTo(word);
            }

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushByte(byte value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            word.Clear();
            word[31] = value;

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        private static readonly byte[] OneStackItem = { 1 };

        public void PushOne()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(OneStackItem);

            int start = Head * 32;
            Span<byte> word = _bytes.Slice(start, 32);
            word.Clear();
            word[31] = 1;

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        private static readonly byte[] ZeroStackItem = { 0 };

        public void PushZero()
        {
            if (_tracer.IsTracingInstructions)
            {
                _tracer.ReportStackPush(ZeroStackItem);
            }

            _bytes.Slice(Head * 32, 32).Clear();

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushUInt32(in int value)
        {
            Span<byte> word = _bytes.Slice(Head * 32, 28);
            word.Clear();

            Span<byte> intPlace = _bytes.Slice(Head * 32 + 28, 4);
            BinaryPrimitives.WriteInt32BigEndian(intPlace, value);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
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
            Span<byte> word = _bytes.Slice(Head * 32, 32);
            ref byte bytes = ref word[0];

            ulong u3 = value.u3;
            ulong u2 = value.u2;
            ulong u1 = value.u1;
            ulong u0 = value.u0;

            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(u3);
                u2 = BinaryPrimitives.ReverseEndianness(u2);
                u1 = BinaryPrimitives.ReverseEndianness(u1);
                u0 = BinaryPrimitives.ReverseEndianness(u0);
            }

            Unsafe.WriteUnaligned(ref bytes, u3);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, sizeof(ulong)), u2);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong)), u1);
            Unsafe.WriteUnaligned(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong)), u0);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushSignedInt256(in Int256.Int256 value)
        {
            PushUInt256((UInt256)value);
        }

        public void PopLimbo()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }
        }

        public void PopSignedInt256(out Int256.Int256 result)
        {
            PopUInt256(out UInt256 value);
            result = new Int256.Int256(value);
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

            ulong u3 = Unsafe.ReadUnaligned<ulong>(ref bytes);
            ulong u2 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, sizeof(ulong)));
            ulong u1 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 2 * sizeof(ulong)));
            ulong u0 = Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref bytes, 3 * sizeof(ulong)));

            if (BitConverter.IsLittleEndian)
            {
                u3 = BinaryPrimitives.ReverseEndianness(u3);
                u2 = BinaryPrimitives.ReverseEndianness(u2);
                u1 = BinaryPrimitives.ReverseEndianness(u1);
                u0 = BinaryPrimitives.ReverseEndianness(u0);
            }

            result = new UInt256(u0, u1, u2, u3);
        }

        public Address PopAddress()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return new Address(_bytes.Slice(Head * 32 + 12, 20).ToArray());
        }

        private ref byte PopBytesByRef()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return ref _bytes[Head * 32];
        }

        // ReSharper disable once ImplicitlyCapturedClosure
        public Span<byte> PopBytes()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return _bytes.Slice(Head * 32, 32);
        }

        public byte PopByte()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return _bytes[Head * 32 + 31];
        }

        public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            if (value.Length != 32)
            {
                _bytes.Slice(Head * 32, 32).Clear();
            }

            value.CopyTo(_bytes.Slice(Head * 32 + 32 - paddingLength, value.Length));

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void Dup(in int depth)
        {
            EnsureDepth(depth);

            ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

            ref byte from = ref Unsafe.Add(ref bytes, (Head - depth) * 32);
            ref byte to = ref Unsafe.Add(ref bytes, Head * 32);

            Unsafe.WriteUnaligned(ref to, Unsafe.ReadUnaligned<Word>(ref from));

            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i >= 0; i--)
                {
                    _tracer.ReportStackPush(_bytes.Slice(Head * 32 - i * 32, 32));
                }
            }

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void EnsureDepth(int depth)
        {
            if (Head < depth)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }
        }

        public void Swap(int depth)
        {
            EnsureDepth(depth);

            ref byte bytes = ref MemoryMarshal.GetReference(_bytes);

            ref byte bottom = ref Unsafe.Add(ref bytes, (Head - depth) * 32);
            ref byte top = ref Unsafe.Add(ref bytes, (Head - 1) * 32);
            
            Word buffer = Unsafe.ReadUnaligned<Word>(ref bottom);
            Unsafe.WriteUnaligned(ref bottom, Unsafe.ReadUnaligned<Word>(ref top));
            Unsafe.WriteUnaligned(ref top, buffer);

            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i > 0; i--)
                {
                    _tracer.ReportStackPush(_bytes.Slice(Head * 32 - i * 32, 32));
                }
            }
        }

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        struct Word { }
        
        public List<string> GetStackTrace()
        {
            List<string> stackTrace = new();
            for (int i = 0; i < Head; i++)
            {
                Span<byte> stackItem = _bytes.Slice(i * 32, 32);
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }
    }
}
