// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Buffers;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Evm;

public class EvmPooledMemory : IEvmMemory
{
    public const int WordSize = 32;

    private static readonly LargerArrayPool Pool = LargerArrayPool.Shared;

    private int _lastZeroedSize;

    private byte[]? _memory;
    public ulong Length { get; private set; }
    public ulong Size { get; private set; }

    public void SaveWord(in UInt256 location, Span<byte> word)
    {
        if (word.Length != WordSize) ThrowArgumentOutOfRangeException();

        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength);
        UpdateSize(newLength);

        int offset = (int)location;

        // Direct 256bit register copy rather than invoke Memmove
        Unsafe.WriteUnaligned(
            ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_memory), offset),
            Unsafe.As<byte, Vector256<byte>>(ref MemoryMarshal.GetReference(word))
        );
    }

    public void SaveByte(in UInt256 location, byte value)
    {
        CheckMemoryAccessViolation(in location, WordSize, out _);
        UpdateSize(in location, in UInt256.One);

        _memory![(long)location] = value;
    }

    public void Save(in UInt256 location, Span<byte> value)
    {
        if (value.Length == 0)
        {
            return;
        }

        CheckMemoryAccessViolation(in location, (ulong)value.Length, out ulong newLength);
        UpdateSize(newLength);

        value.CopyTo(_memory.AsSpan((int)location, value.Length));
    }

    private static void CheckMemoryAccessViolation(in UInt256 location, in UInt256 length, out ulong newLength)
    {
        if (location.IsLargerThanULong() || length.IsLargerThanULong())
        {
            ThrowOutOfGasException();
        }

        CheckMemoryAccessViolation(location.u0, length.u0, out newLength);
    }

    private static void CheckMemoryAccessViolation(in UInt256 location, ulong length, out ulong newLength)
    {
        if (location.IsLargerThanULong())
        {
            ThrowOutOfGasException();
        }

        CheckMemoryAccessViolation(location.u0, length, out newLength);
    }

    private static void CheckMemoryAccessViolation(ulong location, ulong length, out ulong newLength)
    {
        ulong totalSize = location + length;
        if (totalSize < location || totalSize > long.MaxValue)
        {
            ThrowOutOfGasException();
        }

        newLength = totalSize;
    }

    public void Save(in UInt256 location, byte[] value)
    {
        if (value.Length == 0)
        {
            return;
        }

        UInt256 length = (UInt256)value.Length;
        CheckMemoryAccessViolation(in location, in length, out ulong newLength);
        UpdateSize(newLength);

        Array.Copy(value, 0, _memory!, (long)location, value.Length);
    }

    public void Save(in UInt256 location, in ZeroPaddedSpan value)
    {
        if (value.Length == 0)
        {
            return;
        }

        UInt256 length = (UInt256)value.Length;
        CheckMemoryAccessViolation(in location, in length, out ulong newLength);
        UpdateSize(newLength);

        int intLocation = (int)location;
        value.Span.CopyTo(_memory.AsSpan(intLocation, value.Span.Length));
        _memory.AsSpan(intLocation + value.Span.Length, value.PaddingLength).Clear();
    }

    public Span<byte> LoadSpan(scoped in UInt256 location)
    {
        CheckMemoryAccessViolation(in location, WordSize, out ulong newLength);
        UpdateSize(newLength);

        return _memory.AsSpan((int)location, WordSize);
    }

    public Span<byte> LoadSpan(scoped in UInt256 location, scoped in UInt256 length)
    {
        if (length.IsZero)
        {
            return Array.Empty<byte>();
        }

        CheckMemoryAccessViolation(in location, in length, out ulong newLength);
        UpdateSize(newLength);

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

        UpdateSize(in location, in length);

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

        CheckMemoryAccessViolation(in location, in length, out ulong newSize);

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

            UpdateSize(newSize, rentIfNeeded: false);

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
            _memory = null;
        }
    }

    public static long Div32Ceiling(in UInt256 length)
    {
        if (length.IsLargerThanULong())
        {
            ThrowOutOfGasException();
        }

        ulong result = length.u0;
        ulong rem = result & 31;
        result >>= 5;
        if (rem > 0)
        {
            result++;
        }

        if (result > int.MaxValue)
        {
            ThrowOutOfGasException();
        }

        return (long)result;
    }

    private void UpdateSize(in UInt256 location, in UInt256 length, bool rentIfNeeded = true)
    {
        UpdateSize((ulong)(location + length), rentIfNeeded);
    }

    private void UpdateSize(ulong length, bool rentIfNeeded = true)
    {
        Length = length;
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
    private static void ThrowArgumentOutOfRangeException()
    {
        Metrics.EvmExceptions++;
        throw new ArgumentOutOfRangeException("Word size must be 32 bytes");
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowOutOfGasException()
    {
        Metrics.EvmExceptions++;
        throw new OutOfGasException();
    }
}

internal static class UInt256Extensions
{
    public static bool IsLargerThanULong(in this UInt256 value)
    {
        return (value.u1 | value.u2 | value.u3) != 0;
    }
}
