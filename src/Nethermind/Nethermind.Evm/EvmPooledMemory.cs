/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Numerics;
using Nethermind.Core.Extensions;
using Nethermind.Core.Model;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Store;

namespace Nethermind.Evm
{
    public class EvmPooledMemory : IEvmMemory
    {
        public const int WordSize = 32;
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        private static readonly byte[] EmptyBytes = new byte[0];

        private int _lastZeroedSize;

        private byte[] _memory;
        public ulong Length { get; private set; }
        public ulong Size { get; private set; }

        public void SaveWord(UInt256 location, byte[] word)
        {
            SaveWord(location, word.AsSpan());
        }

        public void SaveWord(UInt256 location, Span<byte> word)
        {
            CheckMemoryAccessViolation(location, WordSize);
            UpdateSize(location, WordSize);

            if (word.Length < WordSize)
            {
                Array.Clear(_memory, (int)location, WordSize - word.Length);
            }

            word.CopyTo(_memory.AsSpan().Slice((int)location + WordSize - word.Length, word.Length));
        }

        public void SaveByte(UInt256 location, byte value)
        {
            CheckMemoryAccessViolation(location, WordSize);
            UpdateSize(location, 1);

            _memory[(long)location] = value;
        }

        public void SaveByte(UInt256 location, byte[] value)
        {
            CheckMemoryAccessViolation(location, WordSize);
            UpdateSize(location, 1);

            _memory[(long)location] = value[value.Length - 1];
        }

        public void Save(UInt256 location, Span<byte> value)
        {
            if (value.Length == 0)
            {
                return;
            }
            
            CheckMemoryAccessViolation(location, (UInt256)value.Length);
            UpdateSize(location, (UInt256)value.Length);

            value.CopyTo(_memory.AsSpan().Slice((int)location, value.Length));
        }

        private static void CheckMemoryAccessViolation(UInt256 location, UInt256 length)
        {
            UInt256 totalSize = location + length; // TODO: add with overflow check
            if (totalSize < location || totalSize > long.MaxValue)
            {
                Metrics.EvmExceptions++;
                throw new EvmAccessViolationException(); // TODO: memory range error code
            }
        }

        public void Save(UInt256 location, byte[] value)
        {
            if (value.Length == 0)
            {
                return;
            }
            
            CheckMemoryAccessViolation(location, (UInt256)value.Length);
            UpdateSize(location, (UInt256)value.Length);

            Array.Copy(value, 0, _memory, (long)location, value.Length);
        }

        public byte[] Load(UInt256 location)
        {
            CheckMemoryAccessViolation(location, (UInt256)WordSize);
            UpdateSize(location, WordSize);

            byte[] buffer = new byte[WordSize];
            Array.Copy(_memory, (long)location, buffer, 0, buffer.Length);
            return buffer;
        }
        
        public Span<byte> LoadSpan(UInt256 location)
        {
            CheckMemoryAccessViolation(location, WordSize);
            UpdateSize(location, WordSize);
            
            return _memory.AsSpan().Slice((int)location, WordSize);
        }

        public Span<byte> LoadSpan(UInt256 location, UInt256 length)
        {
            if (length.IsZero)
            {
                return EmptyBytes;
            }

            CheckMemoryAccessViolation(location, length);
            UpdateSize(location, length);

            return _memory.AsSpan().Slice((int)location, (int)length);
        }

        public byte[] Load(UInt256 location, UInt256 length)
        {
            if (length.IsZero)
            {
                return EmptyBytes;
            }

            if (location > long.MaxValue)
            {
                return new byte[(long)length];
            }
            
            UpdateSize(location, length);

            byte[] buffer = new byte[(int)length];
            Array.Copy(_memory, (long)location, buffer, 0, buffer.Length);
            return buffer;
        }

        public long CalculateMemoryCost(UInt256 location, UInt256 length)
        {
            if (length.IsZero)
            {
                return 0L;
            }

            CheckMemoryAccessViolation(location, length);
            UInt256 newSize = location + length;

            if (newSize > Size)
            {
                long newActiveWords = Div32Ceiling(newSize);
                long activeWords = Div32Ceiling(Size);

                // TODO: guess it would be well within ranges but this needs to be checked and comment need to be added with calculations
                UInt256 cost = (ulong)
                    ((newActiveWords - activeWords) * GasCostOf.Memory +
                     ((newActiveWords * newActiveWords) >> 9) -
                     ((activeWords * activeWords) >> 9));

                if (cost > long.MaxValue)
                {
                    return long.MaxValue;
                }
                
                UpdateSize((ulong)newSize, 0, false);

                return (long)cost;
            }

            return 0L;
        }

        public List<string> GetTrace()
        {
            int traceLocation = 0;
            List<string> memoryTrace = new List<string>();
            if (_memory != null)
            {
                while ((ulong)traceLocation < Size)
                {
                    int sizeAvailable = Math.Min(WordSize, (int)Size - traceLocation);
                    memoryTrace.Add(_memory.Slice(traceLocation, sizeAvailable).ToHexString());
                    traceLocation = traceLocation + WordSize;
                }
            }

            return memoryTrace;
        }

        public void Dispose()
        {
            if (_memory != null)
            {
                Pool.Return(_memory);
            }
        }

        public static long Div32Ceiling(UInt256 length)
        {
            UInt256 rem = length & 31;
            UInt256 result = length >> 5;
            UInt256 withCeiling = result + (rem.IsZero ? 0UL : 1UL);
            if (withCeiling > VirtualMachine.BigIntMaxInt)
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }
            
            return (long)withCeiling;
        }

        private void UpdateSize(UInt256 position, UInt256 length, bool rentIfNeeded = true)
        {
            Length = (ulong)(position + length);
            if (Length > Size)
            {
                ulong remainder = Length % WordSize;
                if (remainder != 0)
                {
                    Size = Length + WordSize - remainder;
                }
                else
                {
                    Size = Length;
                }
            }

            if (rentIfNeeded)
            {
                if (_memory == null)
                {
                    _memory = Pool.Rent((int)Size);
                    Array.Clear(_memory, 0, (int)Size);
                }
                else if (Size > (ulong)_memory.LongLength)
                {
                    byte[] beforeResize = _memory;
                    _memory = Pool.Rent((int)Size);
                    Array.Copy(beforeResize, 0, _memory, 0, _lastZeroedSize);
                    Array.Clear(_memory, _lastZeroedSize, (int)Size - _lastZeroedSize);
                    Pool.Return(beforeResize);
                }
                else if (Size > (ulong)_lastZeroedSize)
                {
                    Array.Clear(_memory, _lastZeroedSize, (int)Size - _lastZeroedSize);
                }

                _lastZeroedSize = (int)Size;
            }
        }
    }
}