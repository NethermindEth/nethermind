// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm
{
    public class EvmPooledMemory : IEvmMemory
    {
        public const int WordSize = 32;
        private static readonly UInt256 WordSize256 = WordSize;

        private static readonly ArrayPool<byte> Pool = LargerArrayPool.Shared;

        private int _lastZeroedSize;

        private byte[]? _memory;
        public ulong Length { get; private set; }
        public ulong Size { get; private set; }

        public void SaveWord(in UInt256 location, Span<byte> word)
        {
            CheckMemoryAccessViolation(in location, in WordSize256);
            UpdateSize(in location, in WordSize256);

            int offset = (int)location;
            if (word.Length == WordSize)
            {
                // Direct 256bit register copy rather than invoke Memmove
                Unsafe.WriteUnaligned(
                    ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory), offset),
                    Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(word))
                );
            }
            else if (word.Length < WordSize)
            {
                Array.Clear(_memory!, offset, WordSize - word.Length);
                word.CopyTo(_memory.AsSpan(offset + WordSize - word.Length, word.Length));
            }
            else
            {
                // Should never be possible, but just for completeness of if states
                ThrowOutOfGasException();
            }
        }

        public void SaveByte(in UInt256 location, byte value)
        {
            CheckMemoryAccessViolation(in location, in WordSize256);
            UpdateSize(in location, in UInt256.One);

            _memory![(long)location] = value;
        }

        public void Save(in UInt256 location, Span<byte> value)
        {
            if (value.Length == 0)
            {
                return;
            }

            UInt256 length = (UInt256)value.Length;
            CheckMemoryAccessViolation(in location, in length);
            UpdateSize(in location, in length);

            value.CopyTo(_memory.AsSpan((int)location, value.Length));
        }

        private static void CheckMemoryAccessViolation(in UInt256 location, in UInt256 length)
        {
            UInt256 totalSize = location + length;
            if (totalSize < location || totalSize > long.MaxValue)
            {
                ThrowOutOfGasException();
            }
        }

        public void Save(in UInt256 location, byte[] value)
        {
            if (value.Length == 0)
            {
                return;
            }

            UInt256 length = (UInt256)value.Length;
            CheckMemoryAccessViolation(in location, in length);
            UpdateSize(in location, in length);

            Array.Copy(value, 0, _memory!, (long)location, value.Length);
        }

        public void Save(in UInt256 location, ZeroPaddedSpan value)
        {
            if (value.Length == 0)
            {
                return;
            }

            UInt256 length = (UInt256)value.Length;
            CheckMemoryAccessViolation(in location, in length);
            UpdateSize(in location, in length);

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

            UInt256 length = (UInt256)value.Length;
            CheckMemoryAccessViolation(in location, in length);
            UpdateSize(in location, in length);

            int intLocation = (int)location;
            value.Memory.CopyTo(_memory.AsMemory().Slice(intLocation, value.Memory.Length));
            _memory.AsSpan(intLocation + value.Memory.Length, value.PaddingLength).Clear();
        }

        public Span<byte> LoadSpan(scoped in UInt256 location)
        {
            CheckMemoryAccessViolation(in location, in WordSize256);
            UpdateSize(in location, in WordSize256);

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

            CheckMemoryAccessViolation(in location, in length);
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

                UpdateSize(in newSize, in UInt256.Zero, false);

                return (long)cost;
            }

            return 0L;
        }

        public List<string> GetTrace()
        {
            int traceLocation = 0;
            List<string> memoryTrace = new();

            while ((ulong)traceLocation < Size)
            {
                int sizeAvailable = Math.Min(WordSize, (_memory?.Length ?? 0) - traceLocation);
                if (sizeAvailable > 0)
                {
                    Span<byte> bytes = _memory.AsSpan(traceLocation, sizeAvailable);
                    memoryTrace.Add(bytes.ToHexString());
                }
                else // Memory might not be initialized
                {
                    memoryTrace.Add(Bytes.Zero32.ToHexString());
                }

                traceLocation += WordSize;
            }

            return memoryTrace;
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
            if (!rem.IsZero)
            {
                result += UInt256.One;
            }

            if (result > MaxInt32)
            {
                ThrowOutOfGasException();
            }

            return (long)result;
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

        [DoesNotReturn]
        [StackTraceHidden]
        private static void ThrowOutOfGasException()
        {
            Metrics.EvmExceptions++;
            throw new OutOfGasException();
        }
    }
}
