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
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Store;

namespace Nethermind.Evm
{
    public class EvmPooledMemory : IEvmMemory
    {
        public const int WordSize = 32;
        private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Shared;

        private static readonly byte[] EmptyBytes = new byte[0];

        private long _lastZeroedSize;

        private byte[] _memory;
        public long Length { get; private set; }
        public long Size { get; private set; }

        public void SaveWord(BigInteger location, byte[] word)
        {
            long longLocation = (long)location;
            UpdateSize(longLocation, 1);

            if (word.Length < WordSize)
            {
                Array.Clear(_memory, (int)longLocation, WordSize - word.Length);
            }

            Array.Copy(word, 0, _memory, longLocation + WordSize - word.Length, word.Length);
        }

        public void SaveByte(BigInteger location, byte[] value)
        {
            long longLocation = (long)location;
            UpdateSize(longLocation, 1);

            _memory[longLocation] = value[value.Length - 1];
        }

        public void Save(BigInteger location, byte[] value)
        {
            if (value.Length == 0)
            {
                return;
            }

            long longLocation = (long)location;
            UpdateSize(longLocation, value.Length);

            Array.Copy(value, 0, _memory, longLocation, value.Length);
        }

        public byte[] Load(BigInteger location)
        {
            long longLocation = (long)location;
            UpdateSize(longLocation, WordSize);

            byte[] buffer = new byte[WordSize];
            Array.Copy(_memory, longLocation, buffer, 0, buffer.Length);
            return buffer;
        }

        public byte[] Load(BigInteger location, BigInteger length)
        {
            if (length.IsZero)
            {
                return EmptyBytes;
            }

            long longLocation = (long)location;
            UpdateSize(longLocation, (long)length);

            byte[] buffer = new byte[(int)length];
            Array.Copy(_memory, longLocation, buffer, 0, buffer.Length);
            return buffer;
        }

        public long CalculateMemoryCost(BigInteger position, BigInteger length)
        {
            if (length.IsZero)
            {
                return 0L;
            }

            BigInteger roughPosition = position + length;
            if (roughPosition > int.MaxValue)
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            if (roughPosition > Size)
            {
                long newActiveWords = Div32Ceiling(roughPosition);
                long activeWords = Div32Ceiling(Size);
                //BigInteger cost = (newActiveWords - activeWords) * GasCostOf.Memory +
                //                  BigInteger.Divide(BigInteger.Pow(newActiveWords, 2), 512) -
                //                  BigInteger.Divide(BigInteger.Pow(activeWords, 2), 512);

                // TODO: guess it would be well within ranges but this needs to be checked and comment need to be added with calculations
                BigInteger cost = (newActiveWords - activeWords) * GasCostOf.Memory +
                                  newActiveWords * newActiveWords / 512 -
                                  activeWords * activeWords / 512;

                if (cost > long.MaxValue)
                {
                    Metrics.EvmExceptions++;
                    throw new OutOfGasException();
                }

                UpdateSize((long)roughPosition, 0, false);

                return (long)cost;
            }

            return 0L;
        }

        public List<string> GetTrace()
        {
            int tracePosition = 0;
            List<string> memoryTrace = new List<string>();
            if (_memory != null)
            {
                while (tracePosition < Size)
                {
                    int sizeAvailable = Math.Min(WordSize, (int)Size - tracePosition);
                    memoryTrace.Add(new Hex(_memory.Slice(tracePosition, sizeAvailable)));
                    tracePosition = tracePosition + WordSize;
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

        public static long Div32Ceiling(BigInteger length)
        {
            BigInteger result = BigInteger.DivRem(length, VirtualMachine.BigInt32, out BigInteger rem);
            BigInteger withCeiling = result + (rem > BigInteger.Zero ? BigInteger.One : BigInteger.Zero);
            if (withCeiling > VirtualMachine.BigIntMaxInt)
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            return (long)withCeiling;
        }

        private void UpdateSize(long position, long length, bool rentIfNeeded = true)
        {
            Length = position + length;
            if (Length > Size)
            {
                long remainder = Length % 32;
                if (remainder != 0)
                {
                    Size = Length + 32L - remainder;
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
                else if (Size > _memory.LongLength)
                {
                    byte[] beforeResize = _memory;
                    _memory = Pool.Rent((int)Size);
                    Array.Copy(beforeResize, 0, _memory, 0, _lastZeroedSize);
                    Array.Clear(_memory, (int)_lastZeroedSize, (int)Size - (int)_lastZeroedSize);
                    Pool.Return(beforeResize);
                }
                else if (Size > _lastZeroedSize)
                {
                    Array.Clear(_memory, (int)_lastZeroedSize, (int)Size - (int)_lastZeroedSize);
                }

                _lastZeroedSize = Size;
            }
        }
    }
}