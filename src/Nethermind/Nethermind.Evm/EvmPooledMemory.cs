// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public class EvmPooledMemory : IEvmMemory
    {
        public const int WordSize = 32;

        private static readonly ArrayPool<byte> Pool = LargerArrayPool.Shared;

        private int _lastZeroedSize;

        private byte[]? _memory;
        public ulong Length { get; private set; }
        public ulong Size { get; private set; }

        public void SaveWord(in UInt256 location, Span<byte> word)
        {
            CheckMemoryAccessViolation(in location, WordSize);
            UpdateSize(in location, WordSize);

            if (word.Length < WordSize)
            {
                Array.Clear(_memory!, (int)location, WordSize - word.Length);
            }

            word.CopyTo(_memory.AsSpan((int)location + WordSize - word.Length, word.Length));
        }

        public void SaveByte(in UInt256 location, byte value)
        {
            CheckMemoryAccessViolation(in location, WordSize);
            UpdateSize(in location, 1);

            _memory![(long)location] = value;
        }

        public void Save(in UInt256 location, Span<byte> value)
        {
            if (value.Length == 0)
            {
                return;
            }

            CheckMemoryAccessViolation(in location, (UInt256)value.Length);
            UpdateSize(in location, (UInt256)value.Length);

            value.CopyTo(_memory.AsSpan((int)location, value.Length));
        }

        private static void CheckMemoryAccessViolation(in UInt256 location, in UInt256 length)
        {
            UInt256 totalSize = location + length;
            if (totalSize < location || totalSize > long.MaxValue)
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }
        }

        public void Save(in UInt256 location, byte[] value)
        {
            if (value.Length == 0)
            {
                return;
            }

            CheckMemoryAccessViolation(in location, (UInt256)value.Length);
            UpdateSize(in location, (UInt256)value.Length);

            Array.Copy(value, 0, _memory!, (long)location, value.Length);
        }

        public void Save(in UInt256 location, ZeroPaddedSpan value)
        {
            if (value.Length == 0)
            {
                return;
            }

            CheckMemoryAccessViolation(in location, (UInt256)value.Length);
            UpdateSize(in location, (UInt256)value.Length);

            int intLocation = (int)location;
            value.Span.CopyTo(_memory.AsSpan(intLocation, value.Span.Length));
            _memory.AsSpan(intLocation + value.Span.Length, value.PaddingLength).Clear();
        }

        public void Save(in UInt256 location, ZeroPaddedMemory value)
        {
            if (value.Length == 0)
            {
                return;
            }

            CheckMemoryAccessViolation(in location, (UInt256)value.Length);
            UpdateSize(in location, (UInt256)value.Length);

            int intLocation = (int)location;
            value.Memory.CopyTo(_memory.AsMemory().Slice(intLocation, value.Memory.Length));
            _memory.AsSpan(intLocation + value.Memory.Length, value.PaddingLength).Clear();
        }

        public Span<byte> LoadSpan(scoped in UInt256 location)
        {
            CheckMemoryAccessViolation(in location, WordSize);
            UpdateSize(in location, WordSize);

            return _memory.AsSpan((int)location, WordSize);
        }

        public Span<byte> LoadSpan(in UInt256 location, in UInt256 length)
        {
            if (length.IsZero)
            {
                return Array.Empty<byte>();
            }

            CheckMemoryAccessViolation(in location, length);
            UpdateSize(in location, length);

            return _memory.AsSpan((int)location, (int)length);
        }

        public ReadOnlyMemory<byte> Load(in UInt256 location, in UInt256 length)
        {
            if (length.IsZero)
            {
                return default;
            }

            if (location > int.MaxValue)
            {
                return new byte[(long)length];
            }

            UpdateSize(in location, length);

            return _memory.AsMemory((int)location, (int)length);
        }

        public ReadOnlyMemory<byte> Inspect(in UInt256 location, in UInt256 length)
        {
            if (length.IsZero)
            {
                return default;
            }

            if (location > int.MaxValue)
            {
                return new byte[(long)length];
            }

            if (_memory is null || location + length > _memory.Length)
            {
                return default;
            }

            return _memory.AsMemory((int)location, (int)length);
        }

        public long CalculateMemoryCost(in UInt256 location, in UInt256 length)
        {
            if (length.IsZero)
            {
                return 0L;
            }

            CheckMemoryAccessViolation(in location, length);
            UInt256 newSize = location + length;

            if (newSize > Size)
            {
                long newActiveWords = Div32Ceiling(newSize);
                long activeWords = Div32Ceiling(Size);

                // TODO: guess it would be well within ranges but this needs to be checked and comment need to be added with calculations
                ulong cost = (ulong)
                    ((newActiveWords - activeWords) * GasCostOf.Memory +
                     ((newActiveWords * newActiveWords) >> 9) -
                     ((activeWords * activeWords) >> 9));

                if (cost > long.MaxValue)
                {
                    return long.MaxValue;
                }

                UpdateSize(in newSize, 0, false);

                return (long)cost;
            }

            return 0L;
        }

        public IEnumerable<string> GetTrace()
        {
            int traceLocation = 0;

            while ((ulong)traceLocation < Size)
            {
                int sizeAvailable = Math.Min(WordSize, (_memory?.Length ?? 0) - traceLocation);
                if (sizeAvailable > 0)
                {
                    Span<byte> bytes = _memory.AsSpan(traceLocation, sizeAvailable);

                    yield return bytes.ToHexString();
                }
                else // Memory might not be initialized
                {
                    yield return Bytes.Zero32.ToHexString();
                }

                traceLocation += WordSize;
            }
        }

        public void Dispose()
        {
            if (_memory is not null)
            {
                Pool.Return(_memory);
            }
        }

        private static UInt256 MaxInt32 = (UInt256)int.MaxValue;

        public static long Div32Ceiling(in UInt256 length)
        {
            UInt256 rem = length & 31;
            UInt256 result = length >> 5;
            UInt256 withCeiling = result + (rem.IsZero ? 0UL : 1UL);
            if (withCeiling > MaxInt32)
            {
                Metrics.EvmExceptions++;
                throw new OutOfGasException();
            }

            return (long)withCeiling;
        }

        private void UpdateSize(in UInt256 position, in UInt256 length, bool rentIfNeeded = true)
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
                if (_memory is null)
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
