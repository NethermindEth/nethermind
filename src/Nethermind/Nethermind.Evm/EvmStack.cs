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
using Newtonsoft.Json.Converters;

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
            Register = MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref words[MaxStackSize]), Word.Size);
            Register.Clear();
        }

        public int Head;

        public Span<byte> Register;

        private Span<Word> _words;

        private ITxTracer _tracer;

        public void PushBytes(in Span<byte> value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = AsSpan(Head);
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

            Span<byte> word = AsSpan(Head);
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

        public void PushByte(byte value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            Span<byte> word = AsSpan(Head);
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

            Span<byte> word = AsSpan(Head);
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

            AsSpan(Head).Clear();

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

            ref byte dest = ref Unsafe.Add(ref Unsafe.As<Word, byte>(ref word), 28);

            if (BitConverter.IsLittleEndian)
            {
                value = BinaryPrimitives.ReverseEndianness(value);
            }
            
            Unsafe.WriteUnaligned(ref dest, value);
            
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(AsSpan(Head));

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
            Span<byte> word = AsSpan(Head);
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

            return new Address(AsSpan(Head).Slice(12).ToArray());
        }

        private ref byte PopBytesByRef()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return ref Unsafe.As<Word, byte>(ref _words[Head]);
        }

        // ReSharper disable once ImplicitlyCapturedClosure
        public Span<byte> PopBytes()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return AsSpan(Head);
        }

        public byte PopByte()
        {
            if (Head-- == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            return Unsafe.Add(ref Unsafe.As<Word, byte>(ref _words[Head]), 31);
        }

        public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            if (value.Length != 32)
            {
                _words[Head] = default;
            }

            value.CopyTo(AsSpan(Head).Slice(32 - paddingLength, value.Length));

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
                    _tracer.ReportStackPush(AsSpan(Head - i));
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
                    _tracer.ReportStackPush(AsSpan(Head - i));
                }
            }
        }

        public List<string> GetStackTrace()
        {
            List<string> stackTrace = new List<string>();
            for (int i = 0; i < Head; i++)
            {
                Span<byte> stackItem = AsSpan(i);
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        Span<byte> AsSpan(int index) => MemoryMarshal.CreateSpan(ref Unsafe.As<Word, byte>(ref _words[index]), Word.Size);
    }

    [StructLayout(LayoutKind.Explicit, Size = Size)]
    public struct Word
    {
        public const int Size = 32;
    }
}
