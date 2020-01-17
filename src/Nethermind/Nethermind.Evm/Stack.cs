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
using System.Collections.Generic;
using System.Numerics;
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

            if (value.Length != 32)
            {
                _bytes.Slice(Head * 32, 32 - value.Length).Clear();
            }

            value.CopyTo(_bytes.Slice(Head * 32 + (32 - value.Length), value.Length));
            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushByte(byte value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(new byte[] {value});

            _bytes.Slice(Head * 32, 32).Clear();
            _bytes[Head * 32 + 31] = value;
            Head++;

            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushOne()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(new byte[] {1});

            _bytes.Slice(Head * 32, 32).Clear();
            _bytes[Head * 32 + 31] = 1;
            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushZero()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(new byte[] {0});

            _bytes.Slice(Head * 32, 32).Clear();
            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushUInt256(ref UInt256 value)
        {
            Span<byte> target = _bytes.Slice(Head * 32, 32);
            value.ToBigEndian(target);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(target);

            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushUInt(ref BigInteger value)
        {
            Span<byte> target = _bytes.Slice(Head * 32, 32);
            int bytesToWrite = value.GetByteCount(true);
            if (bytesToWrite != 32)
            {
                target.Clear();
                target = target.Slice(32 - bytesToWrite, bytesToWrite);
            }

            value.TryWriteBytes(target, out int bytesWritten, true, true);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(target);

            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushSignedInt(ref BigInteger value)
        {
            Span<byte> target = _bytes.Slice(Head * 32, 32);
            int bytesToWrite = value.GetByteCount(false);
            bool treatAsUnsigned = bytesToWrite == 33;
            if (treatAsUnsigned)
            {
                bytesToWrite = 32;
            }

            if (bytesToWrite != 32)
            {
                if (value.Sign >= 0)
                {
                    target.Clear();
                }
                else
                {
                    target.Fill(0xff);
                }

                target = target.Slice(32 - bytesToWrite, bytesToWrite);
            }

            value.TryWriteBytes(target, out int bytesWritten, treatAsUnsigned, true);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(target);

            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PopLimbo()
        {
            if (Head == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            Head--;
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
            result = PopBytes().ToSignedBigInteger(32);
        }

        public Address PopAddress()
        {
            if (Head == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            Head--;

            return new Address(_bytes.Slice(Head * 32 + 12, 20).ToArray());
        }

        // ReSharper disable once ImplicitlyCapturedClosure
        public Span<byte> PopBytes()
        {
            if (Head == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            Head--;

            return _bytes.Slice(Head * 32, 32);
        }

        public byte PopByte()
        {
            if (Head == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            Head--;

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
            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void Dup(in int depth)
        {
            if (Head < depth)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            _bytes.Slice((Head - depth) * 32, 32).CopyTo(_bytes.Slice(Head * 32, 32));
            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i >= 0; i--)
                {
                    _tracer.ReportStackPush(_bytes.Slice(Head * 32 - i * 32, 32));
                }
            }

            Head++;
            if (Head >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }
        
        public void Swap(int depth)
        {
            Span<byte> buffer = stackalloc byte[32];
            
            if (Head < depth)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

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
                Span<byte> stackItem =  _bytes.Slice(i * 32, 32);
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }
    }
}