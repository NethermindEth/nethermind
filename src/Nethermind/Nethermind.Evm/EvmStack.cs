//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm.Tracing;

namespace Nethermind.Evm
{
    public ref struct EvmStack
    {
        public const int RegisterLength = 1;
        public const int MaxStackSize = 1025;

        [StructLayout(LayoutKind.Explicit, Size = 32)]
        struct Word
        {
            [FieldOffset(31)]
            public byte Last;

            public static void CopyAsBigEndian(ref UInt256 value, ref Word word)
            {
                ref ulong l = ref Unsafe.As<Word, ulong>(ref word);

                if (BitConverter.IsLittleEndian)
                {
                    l = BinaryPrimitives.ReverseEndianness(value.S3);
                    Unsafe.Add(ref l, 1) = BinaryPrimitives.ReverseEndianness(value.S2);
                    Unsafe.Add(ref l, 2) = BinaryPrimitives.ReverseEndianness(value.S1);
                    Unsafe.Add(ref l, 3) = BinaryPrimitives.ReverseEndianness(value.S0);
                }
                else
                {
                    l = value.S3;
                    Unsafe.Add(ref l, 1) = value.S2;
                    Unsafe.Add(ref l, 2) = value.S1;
                    Unsafe.Add(ref l, 3) = value.S0;
                }
            }
        }

        public EvmStack(in Span<byte> bytes, in int head, ITxTracer txTracer)
        {
            _bytes = bytes;
            _words = MemoryMarshal.Cast<byte, Word>(bytes);
            Head = head;
            _tracer = txTracer;
            Register = _bytes.Slice(MaxStackSize * 32, 32);
            Register.Clear();
        }

        public int Head;

        public Span<byte> Register;

        private Span<byte> _bytes;
        private Span<Word> _words;

        private ITxTracer _tracer;
        
        static readonly byte[] BytesOne = { 1 };
        static readonly byte[] BytesZero = { 0 };

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
                ThrowOverflow();
            }
        }

        public void PushByte(byte value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(MemoryMarshal.CreateSpan(ref value, 1));

            ref Word word = ref _words[Head];
            word = default;
            word.Last = value;

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void PushOne()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(BytesOne);

            ref Word word = ref _words[Head];
            word = default;
            word.Last = 1;

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void PushZero()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(BytesZero);

            _words[Head] = default;

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void PushUInt256(ref UInt256 value)
        {
            Word.CopyAsBigEndian(ref value, ref _words[Head]);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(_bytes.Slice(Head * 32, 32));

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void PushSignedInt(in BigInteger value)
        {
            int sign = value.Sign;
            if (sign == 0)
            {
                PushZero();
                return;
            }

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            if (sign > 0)
            {
                word.Clear();
            }
            else
            {
                word.Fill(0xff);
            }

            Span<byte> fullBytes = stackalloc byte[33];
            value.TryWriteBytes(fullBytes, out int bytesWritten, false, true);

            if (bytesWritten == 33)
            {
                fullBytes.Slice(1, 32).CopyTo(word);
            }
            else
            {
                int fillCount = 32 - bytesWritten;
                fullBytes.Slice(0, bytesWritten).CopyTo(fillCount > 0 ? word.Slice(fillCount, 32 - fillCount) : word);
            }

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void PopLimbo()
        {
            if (Head-- == 0)
            {
                ThrowUnderflow();
            }
        }

        public void PopUInt256(out UInt256 result)
        {
            UInt256.CreateFromBigEndian(out result, PopBytes());
        }

        public void PopUInt(out BigInteger result)
        {
            result = PopBytes().ToUnsignedBigInteger();
        }

        public void PopInt(out BigInteger result)
        {
            result = new BigInteger(PopBytes(), false, true);
        }

        public Address PopAddress()
        {
            if (Head-- == 0)
            {
                ThrowUnderflow();
            }

            return new Address(_bytes.Slice(Head * 32 + 12, 20).ToArray());
        }

        // ReSharper disable once ImplicitlyCapturedClosure

        public Span<byte> PopBytes()
        {
            if (Head-- == 0)
            {
                ThrowUnderflow();
            }

            return _bytes.Slice(Head * 32, 32);
        }

        public byte PopByte()
        {
            if (Head-- == 0)
            {
                ThrowUnderflow();
            }

            return _words[Head].Last;
        }

        public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            if (value.Length != 32)
            {
                _words[Head] = default;
            }

            value.CopyTo(_bytes.Slice(Head * 32 + 32 - paddingLength, value.Length));

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void Dup(in int depth)
        {
            if (Head < depth)
            {
                ThrowUnderflow();
            }

            _words[Head] = _words[Head - depth];

            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i >= 0; i--)
                {
                    _tracer.ReportStackPush(_bytes.Slice(Head * 32 - i * 32, 32));
                }
            }

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        public void Swap(int depth)
        {
            if (Head < depth)
            {
                ThrowUnderflow();
            }

            ref Word bottomWord = ref _words[Head - depth];
            ref Word topWord = ref _words[Head - 1];

            var buffer = bottomWord;
            bottomWord = topWord;
            topWord = buffer;

            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i > 0; i--)
                {
                    _tracer.ReportStackPush(_bytes.Slice(Head * 32 - i * 32, 32));
                }
            }
        }

        public List<string> GetStackTrace()
        {
            List<string> stackTrace = new List<string>();
            for (int i = 0; i < Head; i++)
            {
                Span<byte> stackItem = _bytes.Slice(i * 32, 32);
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }

        public void PushUInt(ref BigInteger value)
        {
            if (value.IsOne)
            {
                PushOne();
                return;
            }

            if (value.IsZero)
            {
                PushZero();
                return;
            }

            Span<byte> word = _bytes.Slice(Head * 32, 32);
            Span<byte> test = stackalloc byte[32];
            value.TryWriteBytes(test, out int bytesWritten, true, true);
            if (bytesWritten == 32)
            {
                test.CopyTo(word);
            }
            else
            {
                word.Clear();
                Span<byte> target = word.Slice(32 - bytesWritten, bytesWritten);
                test.Slice(0, bytesWritten).CopyTo(target);
            }

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            if (++Head >= MaxStackSize)
            {
                ThrowOverflow();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowOverflow()
        {
            Metrics.EvmExceptions++;
            throw new EvmStackOverflowException();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ThrowUnderflow()
        {
            Metrics.EvmExceptions++;
            throw new EvmStackUnderflowException();
        }
    }
}