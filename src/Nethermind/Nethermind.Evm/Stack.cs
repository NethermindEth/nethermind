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

        public EvmStack(in Span<byte> bytes, in int stackHead, ITxTracer txTracer)
        {
            _stack = bytes;
            _stackHead = stackHead;
            _tracer = txTracer;
            Register = _stack.Slice(MaxStackSize * 32, 32);
        }

        private int _stackHead;

        private Span<byte> _stack;

        private ITxTracer _tracer;
        
        public Span<byte> Register;

        private Span<byte> Item(int position)
        {
            return _stack.Slice(position * 32, 32);
        }

        public void PushBytes(in Span<byte> value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            if (value.Length != 32)
            {
                _stack.Slice(_stackHead * 32, 32 - value.Length).Clear();
            }

            value.CopyTo(_stack.Slice(_stackHead * 32 + (32 - value.Length), value.Length));
            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushByte(byte value)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(new byte[] {value});

            _stack.Slice(_stackHead * 32, 32).Clear();
            _stack[_stackHead * 32 + 31] = value;
            _stackHead++;

            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushOne()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(new byte[] {1});

            _stack.Slice(_stackHead * 32, 32).Clear();
            _stack[_stackHead * 32 + 31] = 1;
            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushZero()
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(new byte[] {0});

            _stack.Slice(_stackHead * 32, 32).Clear();
            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushUInt256(ref UInt256 value)
        {
            Span<byte> target = _stack.Slice(_stackHead * 32, 32);
            value.ToBigEndian(target);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(target);

            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushUInt(ref BigInteger value)
        {
            Span<byte> target = _stack.Slice(_stackHead * 32, 32);
            int bytesToWrite = value.GetByteCount(true);
            if (bytesToWrite != 32)
            {
                target.Clear();
                target = target.Slice(32 - bytesToWrite, bytesToWrite);
            }

            value.TryWriteBytes(target, out int bytesWritten, true, true);

            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(target);

            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PushSignedInt(ref BigInteger value)
        {
            Span<byte> target = _stack.Slice(_stackHead * 32, 32);
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

            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void PopLimbo()
        {
            if (_stackHead == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            _stackHead--;
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
            if (_stackHead == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            _stackHead--;

            return new Address(_stack.Slice(_stackHead * 32 + 12, 20).ToArray());
        }

        // ReSharper disable once ImplicitlyCapturedClosure
        public Span<byte> PopBytes()
        {
            if (_stackHead == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            _stackHead--;

            return _stack.Slice(_stackHead * 32, 32);
        }

        public byte PopByte()
        {
            if (_stackHead == 0)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            _stackHead--;

            return _stack[_stackHead * 32 + 31];
        }

        public void PushLeftPaddedBytes(Span<byte> value, int paddingLength)
        {
            if (_tracer.IsTracingInstructions) _tracer.ReportStackPush(value);

            if (value.Length != 32)
            {
                _stack.Slice(_stackHead * 32, 32).Clear();
            }

            value.CopyTo(_stack.Slice(_stackHead * 32 + 32 - paddingLength, value.Length));
            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }

        public void Dup(in int depth)
        {
            if (_stackHead < depth)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            _stack.Slice((_stackHead - depth) * 32, 32).CopyTo(_stack.Slice(_stackHead * 32, 32));
            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i >= 0; i--)
                {
                    _tracer.ReportStackPush(_stack.Slice(_stackHead * 32 - i * 32, 32));
                }
            }

            _stackHead++;
            if (_stackHead >= MaxStackSize)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackOverflowException();
            }
        }
        
        public void Swap(int depth)
        {
            Span<byte> buffer = stackalloc byte[32];
            
            if (_stackHead < depth)
            {
                Metrics.EvmExceptions++;
                throw new EvmStackUnderflowException();
            }

            Span<byte> bottomSpan = _stack.Slice((_stackHead - depth) * 32, 32);
            Span<byte> topSpan = _stack.Slice((_stackHead - 1) * 32, 32);

            bottomSpan.CopyTo(buffer);
            topSpan.CopyTo(bottomSpan);
            buffer.CopyTo(topSpan);

            if (_tracer.IsTracingInstructions)
            {
                for (int i = depth; i > 0; i--)
                {
                    _tracer.ReportStackPush(_stack.Slice(_stackHead * 32 - i * 32, 32));
                }
            }
        }
        
        public List<string> GetStackTrace()
        {
            List<string> stackTrace = new List<string>();
            for (int i = 0; i < _stackHead; i++)
            {
                Span<byte> stackItem = Item(i);
                stackTrace.Add(stackItem.ToArray().ToHexString());
            }

            return stackTrace;
        }
    }
}