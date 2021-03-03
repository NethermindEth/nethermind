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

        public EvmStack(in Span<Word> words, in int head, ITxTracer txTracer)
        {
            _words = words;
            Head = head;
            _tracer = txTracer;
            Register = MemoryMarshal.AsBytes(_words.Slice(MaxStackSize, 1));
            Register.Clear();
        }

        public int Head;

        public Span<byte> Register;

        private Span<Word> _words;

        private ITxTracer _tracer;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Span<byte> AsSpan(ref Word word, int start = 0) => MemoryMarshal.CreateSpan(ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref word), start), Word.Size - start);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Span<byte> AsSpanWithLength(ref Word word, int length) => MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref word), length);

        public void PushBytes(in Span<byte> value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            ref Word word = ref _words[Head];
            if (value.Length != Word.Size)
            {
                word = default;
                value.CopyTo(AsSpan(ref word, value.Length));
            }
            else
            {
                value.CopyTo(AsSpan(ref word));
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

            ref Word word = ref _words[Head];
            ReadOnlySpan<byte> span = value.Span;
            
            if (span.Length != 32)
            {
                word = default;
                span.CopyTo(AsSpanWithLength(ref word, span.Length));
            }
            else
            {
                span.CopyTo(AsSpan(ref word));
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

            ref Word word = ref _words[Head];
            word = default;
            word.LastByte = value;

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

            ref Word word = ref _words[Head];
            word = default;
            word.LastByte = 1;

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

            ref Word word = ref _words[Head];
            word = default;
            
            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushUInt32(int value)
        {
            ref Word word = ref _words[Head];
            word = default;

            if (BitConverter.IsLittleEndian)
            {
                value = BinaryPrimitives.ReverseEndianness(value);
            }

            word = default;
            word.LastInt = value;

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(AsSpan(ref word));

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
            ref Word word = ref _words[Head];

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

            // reverse order of ulongs
            word.U3 = u0;
            word.U2 = u1;
            word.U1 = u2;
            word.U0 = u3;

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(AsSpan(ref word));

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
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            ref Word word = ref _words[Head];

            // reverse order of ulongs
            ulong u3 = word.U0;
            ulong u2 = word.U1;
            ulong u1 = word.U2;
            ulong u0 = word.U3;

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

            return new Address(AsSpan(ref _words[Head], 12).ToArray());
        }

        // ReSharper disable once ImplicitlyCapturedClosure
        public Span<byte> PopBytes()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            
            return AsSpan(ref _words[Head]);
        }

        public byte PopByte()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return _words[Head].LastByte;
        }

        public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            ref Word word = ref _words[Head];

            if (value.Length != 32)
            {
                word = default;
            }

            value.CopyTo(AsSpan(ref word, Word.Size - paddingLength));

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void Dup(in int depth)
        {
            EnsureDepth(depth);

            _words[Head] = _words[Head - depth];
           
            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i >= 0; i--)
                {
                    _tracer.ReportStackPush(AsSpan(ref _words[Head - i]));
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

            ref Word bottom = ref _words[Head - depth];
            ref Word top = ref _words[Head - 1];

            Word buffer = bottom;
            bottom = top;
            top = buffer;
            
            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i > 0; i--)
                {
                    _tracer.ReportStackPush(AsSpan(ref _words[Head - i]));
                }
            }
        }

        public List<string> GetStackTrace()
        {
            List<string> stackTrace = new List<string>();
            for (int i = 0; i < Head; i++)
            {
                Span<byte> stackItem = AsSpan(ref _words[i]);
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }
    }
}
