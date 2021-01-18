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

        private static readonly byte[] OneStackItem = {1};
        
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

        private static readonly byte[] ZeroStackItem = {0};
        
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

        public void PushUInt256(in UInt256 value)
        {
            Span<byte> word = _bytes.Slice(Head * 32, 32);
            value.ToBigEndian(word);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(word);

            if (++Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushSignedInt256(in Int256.Int256 value)
        {
            PushUInt256((UInt256) value);
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
            result = new Int256.Int256(PopBytes(), true);
        }

        public void PopUInt256(out UInt256 result)
        {
            result = new UInt256(PopBytes(), true);
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

            _bytes.Slice((Head - depth) * 32, 32).CopyTo(_bytes.Slice(Head * 32, 32));
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
            Span<byte> buffer = stackalloc byte[32];

            EnsureDepth(depth);

            Span<byte> bottomSpan = _bytes.Slice((Head - depth) * 32, 32);
            Span<byte> topSpan = _bytes.Slice((Head - 1) * 32, 32);

            bottomSpan.CopyTo(buffer);
            topSpan.CopyTo(bottomSpan);
            buffer.CopyTo(topSpan);

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
    }
}
