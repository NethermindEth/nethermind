// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;

namespace Nethermind.Evm;

public struct EvmPooledMemory : IEvmMemory
{
    public const int WordSize = 32;

    private ulong _lastZeroedSize;

    private byte[]? _memory;
    public ulong Length { get; private set; }
    public ulong Size { get; private set; }

    public bool TrySaveWord(in UInt256 location, Span<byte> word)
    {
        if (word.Length != WordSize) ThrowArgumentOutOfRangeException();

        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        UpdateSize(newLength);

        int offset = (int)location;

        // Direct 256bit register copy rather than invoke Memmove
        Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory), offset),
            Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(word))
        );

        return true;
    }

    public bool TrySaveByte(in UInt256 location, byte value)
    {
        // MSTORE8 only touches a single byte; validate against the actual byte length.
        CheckMemoryAccessViolation(in location, 1, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        UpdateSize(newLength);

        _memory![(long)location] = value;
        return true;
    }

    public bool TrySave(in UInt256 location, Span<byte> value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        CheckMemoryAccessViolation(in location, (ulong)value.Length, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        UpdateSize(newLength);

        value.CopyTo(_memory.AsSpan((int)location, value.Length));
        return true;
    }

    private static void CheckMemoryAccessViolation(in UInt256 location, in UInt256 length, out ulong newLength, out bool outOfGas)
    {
        if (length.IsLargerThanULong())
        {
            outOfGas = true;
            newLength = 0;
            return;
        }

        CheckMemoryAccessViolation(in location, length.u0, out newLength, out outOfGas);
    }

    private static void CheckMemoryAccessViolation(in UInt256 location, ulong length, out ulong newLength, out bool outOfGas)
    {
        // Check for overflow and ensure the word-aligned size fits in int.
        // Word alignment can add up to 31 bytes, so we use (int.MaxValue - WordSize + 1) as the limit.
        // This ensures that after word alignment, the size still fits in int for .NET array operations.
        const ulong MaxMemorySize = int.MaxValue - WordSize + 1;

        if (location.IsLargerThanULong() || length > MaxMemorySize)
        {
            outOfGas = true;
            newLength = 0;
            return;
        }

        ulong totalSize = location.u0 + length;
        if (totalSize < location.u0 || totalSize > MaxMemorySize)
        {
            outOfGas = true;
            newLength = 0;
            return;
        }

        outOfGas = false;
        newLength = totalSize;
    }

    public bool TrySave(in UInt256 location, byte[] value)
    {
        if (value.Length == 0)
        {
            return true;
        }

        ulong length = (ulong)value.Length;
        CheckMemoryAccessViolation(in location, length, out ulong newLength, out bool outOfGas);
        if (outOfGas) return false;

        UpdateSize(newLength);

        Array.Copy(value, 0, _memory!, (long)location, value.Length);
        return true;
    }

    public bool TrySave(in UInt256 location, in ZeroPaddedSpan value)
    {
        if (value.Length == 0)
        {
            // Nothing to do
            return true;
        }

        ulong length = (ulong)value.Length;
        CheckMemoryAccessViolation(in location, length, out ulong newLength, out bool outOfGas);
        outOfGas |= location.u0 > int.MaxValue;

        if (outOfGas) return false;

        UpdateSize(newLength);

        int intLocation = (int)location.u0;
        value.Span.CopyTo(_memory.AsSpan(intLocation, value.Span.Length));
        if (value.PaddingLength > 0)
        {
            ClearPadding(_memory, intLocation + value.Span.Length, value.PaddingLength);
        }

        return true;

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void ClearPadding(byte[] memory, int offset, int length)
            => memory.AsSpan(offset, length).Clear();
    }

    public bool TryLoadSpan(scoped in UInt256 location, out Span<byte> data)
    {
        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength, out bool outOfGas);
        if (outOfGas)
        {
            data = default;
            return false;
        }

        UpdateSize(newLength);
        data = _memory.AsSpan((int)location, WordSize);
        return true;
    }

    public bool TryLoadSpan(scoped in UInt256 location, scoped in UInt256 length, out Span<byte> data)
    {
        if (length.IsZero)
        {
            data = [];
            return true;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength, out bool outOfGas);
        if (outOfGas)
        {
            data = default;
            return false;
        }

        UpdateSize(newLength);
        data = _memory.AsSpan((int)location, (int)length);
        return true;
    }

    public bool TryLoad(in UInt256 location, in UInt256 length, out ReadOnlyMemory<byte> data)
    {
        if (length.IsZero)
        {
            data = default;
            return true;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength, out bool outOfGas);
        if (outOfGas)
        {
            data = default;
            return false;
        }

        UpdateSize(newLength);

        data = _memory.AsMemory((int)location, (int)length);
        return true;
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

        if (_memory is null)
        {
            return default;
        }

        if (location >= Size)
        {
            return default;
        }
        UInt256 largeSize = location + length;
        if (largeSize > _memory.Length)
        {
            return default;
        }

        ClearForTracing((ulong)largeSize);
        return _memory.AsMemory((int)location, (int)length);
    }

    private void ClearForTracing(ulong size)
    {
        if (_memory is not null && size > _lastZeroedSize)
        {
            int lengthToClear = (int)(Math.Min(size, (ulong)_memory.Length) - _lastZeroedSize);
            Array.Clear(_memory, (int)_lastZeroedSize, lengthToClear);
            _lastZeroedSize += (uint)lengthToClear;
        }
    }

    public long CalculateMemoryCost(in UInt256 location, in UInt256 length, out bool outOfGas)
    {
        outOfGas = false;
        if (length.IsZero)
        {
            return 0L;
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newSize, out outOfGas);
        if (outOfGas) return 0;

        if (newSize > Size)
        {
            long newActiveWords = EvmCalculations.Div32Ceiling(newSize, out outOfGas);
            if (outOfGas) return 0;
            long activeWords = EvmCalculations.Div32Ceiling(Size, out outOfGas);
            if (outOfGas) return 0;

            // TODO: guess it would be well within ranges but this needs to be checked and comment need to be added with calculations
            ulong cost = (ulong)
                ((newActiveWords - activeWords) * GasCostOf.Memory +
                 ((newActiveWords * newActiveWords) >> 9) -
                 ((activeWords * activeWords) >> 9));

            if (cost > long.MaxValue)
            {
                return long.MaxValue;
            }

            UpdateSize(newSize, rentIfNeeded: false);

            return (long)cost;
        }

        return 0L;
    }

    public TraceMemory GetTrace()
    {
        ulong size = Size;
        ClearForTracing(size);
        return new(size, _memory);
    }

    public void Dispose()
    {
        byte[] memory = _memory;
        if (memory is not null)
        {
            _memory = null;
            ArrayPool<byte>.Shared.Return(memory);
        }
    }

    private void UpdateSize(ulong length, bool rentIfNeeded = true)
    {
        const int MinRentSize = 1_024;
        Length = length;

        if (Length > Size)
        {
            ulong remainder = Length % WordSize;
            Size = remainder != 0 ? Length + WordSize - remainder : Length;
        }

        if (rentIfNeeded)
        {
            if (_memory is null)
            {
                _memory = ArrayPool<byte>.Shared.Rent((int)Math.Max(Size, MinRentSize));
                Array.Clear(_memory, 0, (int)Size);
            }
            else
            {
                int lastZeroedSize = (int)_lastZeroedSize;
                if (Size > (ulong)_memory.LongLength)
                {
                    byte[] beforeResize = _memory;
                    _memory = ArrayPool<byte>.Shared.Rent((int)Size);
                    Array.Copy(beforeResize, 0, _memory, 0, lastZeroedSize);
                    Array.Clear(_memory, lastZeroedSize, (int)(Size - _lastZeroedSize));
                    ArrayPool<byte>.Shared.Return(beforeResize);
                }
                else if (Size > _lastZeroedSize)
                {
                    Array.Clear(_memory, lastZeroedSize, (int)(Size - _lastZeroedSize));
                }
                else
                {
                    return;
                }
            }

            _lastZeroedSize = Size;
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowArgumentOutOfRangeException()
    {
        Metrics.EvmExceptions++;
        throw new ArgumentOutOfRangeException("Word size must be 32 bytes");
    }
}

public static class UInt256Extensions
{
    public static bool IsLargerThanULong(in this UInt256 value) => (value.u1 | value.u2 | value.u3) != 0;
    public static bool IsLargerThanLong(in this UInt256 value) => value.IsLargerThanULong() || value.u0 > long.MaxValue;
    public static long ToLong(in this UInt256 value) => value.IsLargerThanLong() ? long.MaxValue : (long)value.u0;
}
