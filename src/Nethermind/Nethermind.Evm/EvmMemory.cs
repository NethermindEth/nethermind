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
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Evm
{
    public class EvmMemory : IEvmMemory
    {
        public const int WordSize = 32;

        private static readonly byte[] EmptyBytes = new byte[0];
        private readonly MemoryStream _memory = new MemoryStream();

        public long Size { get; private set; }

        public void SaveWord(BigInteger location, Span<byte> word)
        {
            SaveWord(location, word.ToArray());
        }

        public void SaveWord(BigInteger location, byte[] word)
        {
            _memory.Position = (long)location;
            if (word.Length < WordSize)
            {
                byte[] zeros = new byte[WordSize - word.Length];
                _memory.Write(zeros, 0, zeros.Length);
            }

            _memory.Write(word, 0, word.Length);

            UpdateSize();
        }

        public void SaveByte(BigInteger location, byte value)
        {
            _memory.Position = (long)location;
            _memory.WriteByte(value);

            UpdateSize();
        }

        public void SaveByte(BigInteger location, byte[] value)
        {
            _memory.Position = (long)location;
            _memory.WriteByte(value[value.Length - 1]);

            UpdateSize();
        }

        public void Save(BigInteger location, Span<byte> value)
        {
            Save(location, value.ToArray());
        }

        public static long Div32Ceiling(BigInteger length)
        {
            BigInteger result = BigInteger.DivRem(length, VirtualMachine.BigInt32, out BigInteger rem);
            BigInteger withCeiling = result + (rem > BigInteger.Zero ? BigInteger.One : BigInteger.Zero);
            if (withCeiling > VirtualMachine.BigIntMaxInt)
            {
                throw new OutOfGasException();
            }

            return (long)withCeiling;
        }

        public void Save(BigInteger location, byte[] value)
        {
            if (value.Length == 0)
            {
                return;
            }

            _memory.Position = (long)location;
            _memory.Write(value, 0, value.Length);

            UpdateSize();
        }

        public Span<byte> LoadSpan(BigInteger location)
        {
            return Load(location).AsSpan();
        }

        public Span<byte> LoadSpan(BigInteger location, BigInteger length)
        {
            return Load(location, length).AsSpan();
        }

        public byte[] Load(BigInteger location)
        {
            byte[] buffer = new byte[WordSize];
            _memory.Position = (long)location;
            _memory.Read(buffer, 0, WordSize);

            UpdateSize();

            return buffer;
        }

        public byte[] Load(BigInteger location, BigInteger length)
        {
            if (length.IsZero)
            {
                return EmptyBytes;
            }

            long position = (long)location;

            byte[] buffer = new byte[(int)length];
            if (position <= _memory.Length)
            {
                _memory.Position = position;
                _memory.Read(buffer, 0, buffer.Length);
            }

            _memory.Position = position + buffer.Length;

            UpdateSize();

            return buffer;
        }

        private void UpdateSizeWithoutAllocating(long position)
        {
            long memoryLength = position;
            if (memoryLength > Size)
            {
                long remainder = memoryLength % 32;
                if (remainder != 0)
                {
                    memoryLength += 32L - remainder;
                }

                Size = memoryLength;
            }
        }

        private void UpdateSize()
        {
            long memoryLength = _memory.Position;
            if (memoryLength > Size)
            {
                long remainder = memoryLength % 32;
                if (remainder != 0)
                {
                    memoryLength += 32L - remainder;
                }

                Size = memoryLength;
            }
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
                throw new OutOfGasException();
            }

            if (roughPosition > Size)
            {
                long newActiveWords = Div32Ceiling(roughPosition);
                long activeWords = Div32Ceiling(Size);
                BigInteger cost = (newActiveWords - activeWords) * GasCostOf.Memory +
                                  BigInteger.Divide(BigInteger.Pow(newActiveWords, 2), 512) -
                                  BigInteger.Divide(BigInteger.Pow(activeWords, 2), 512);

                if (cost > long.MaxValue)
                {
                    throw new OutOfGasException();
                }

                UpdateSizeWithoutAllocating((long)roughPosition);

                return (long)cost;
            }

            return 0L;
        }
        
        public List<string> GetTrace()
        {
            int tracePosition = 0;
            List<string> memoryTrace = new List<string>();
            byte[] buffer = _memory.GetBuffer();
            while (tracePosition < Size)
            {
                int sizeAvailable = Math.Min(WordSize, buffer.Length - tracePosition);
                memoryTrace.Add(buffer.Slice(tracePosition, sizeAvailable).ToHexString());
                tracePosition = tracePosition + WordSize;
            }

            return memoryTrace;
        }

        public void Dispose()
        {
            _memory?.Dispose();
        }
    }
}