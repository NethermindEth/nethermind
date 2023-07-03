// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
namespace Nethermind.Synchronization.FastBlocks;

internal class FastBlockStatusList
{
    private readonly int[] _statuses;
    private readonly long _length;

    public FastBlockStatusList(long length)
    {
        // Can fit 16 statuses per int, however need to round up the division
        long size = length / 16;
        if (size * 16 < length)
        {
            size++;
        }

        _statuses = new int[size];
        _length = length;
    }

    internal FastBlockStatusList(IList<FastBlockStatus> statuses, bool parallel) : this(statuses.Count)
    {
        if (parallel)
        {
            Parallel.For(0, statuses.Count, i =>
            {
                this[i] = statuses[i];
            });
        }
        else
        {
            for (int i = 0; i < statuses.Count; i++)
            {
                this[i] = statuses[i];
            }
        }
    }

    public FastBlockStatus this[long index]
    {
        get
        {
            GuardLength(index);
            (long position, int shift) = GetValuePosition(index);
            int status = Volatile.Read(ref _statuses[position]);
            return DecodeValue(status, shift);
        }
        private set
        {
            GuardLength(index);
            (long position, int shift) = GetValuePosition(index);
            ref int status = ref _statuses[position];
            int oldValue = Volatile.Read(ref status);
            do
            {
                int newValue = EncodeValue(value, oldValue, shift);
                int currentValue = Interlocked.CompareExchange(ref status, newValue, oldValue);
                if (currentValue == oldValue || DecodeValue(currentValue, shift) == value)
                {
                    // Change has happened
                    break;
                }
                // Change not done, set old value to current value
                oldValue = currentValue;
            } while (true);
        }
    }

    private void GuardLength(long index)
    {
        if ((ulong)index >= (ulong)_length)
        {
            ThrowIndexOutOfRange();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (long position, int shift) GetValuePosition(long index)
    {
        (long position, long shift) = Math.DivRem(index, 16);
        return (position, (int)shift * 2); // 2 bits per status
    }

    public bool TrySet(long index, FastBlockStatus newState) => TrySet(index, newState, out _);

    public bool TrySet(long index, FastBlockStatus newState, out FastBlockStatus previousValue)
    {
        GuardLength(index);

        FastBlockStatus requiredPriorState = newState switch
        {
            FastBlockStatus.Sent => FastBlockStatus.Pending,
            FastBlockStatus.Inserted => FastBlockStatus.Sent,
            FastBlockStatus.Pending => FastBlockStatus.Sent,
            _ => throw new ArgumentOutOfRangeException(nameof(newState), newState, null)
        };

        (long position, int shift) = GetValuePosition(index);
        ref int status = ref _statuses[position];
        int oldValue = Volatile.Read(ref status);
        do
        {
            previousValue = DecodeValue(oldValue, shift);
            if (previousValue != requiredPriorState)
            {
                // Change not possible
                return false;
            }

            int newValue = EncodeValue(newState, oldValue, shift);
            int previousValueInt = Interlocked.CompareExchange(ref status, newValue, oldValue);
            if (previousValueInt == oldValue)
            {
                // Change has happened
                return true;
            }

            previousValue = DecodeValue(previousValueInt, shift);
            if (previousValue == newState)
            {
                // Change has happened but not us
                return false;
            }

            // Change not done, set old value to current value
            oldValue = previousValueInt;
        } while (true);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static FastBlockStatus DecodeValue(int value, int shift) => (FastBlockStatus)((value >> shift) & 0b11);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int EncodeValue(FastBlockStatus newState, int oldValue, int shift) => (oldValue & ~(0b11 << shift)) | ((int)newState << shift);

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowIndexOutOfRange()
    {
        throw new IndexOutOfRangeException();
    }
}
