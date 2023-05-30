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
            if ((ulong)index >= (ulong)_length)
            {
                ThrowIndexOutOfRange();
            }

            (long q, long r) = Math.DivRem(index, 16);
            r *= 2;

            int status = Volatile.Read(ref _statuses[q]);
            return (FastBlockStatus)((status >> (int)r) & 0b11);
        }
        private set
        {
            if ((ulong)index >= (ulong)_length)
            {
                ThrowIndexOutOfRange();
            }

            (long q, long r) = Math.DivRem(index, 16);
            r *= 2;

            ref int status = ref _statuses[q];
            int oldValue = Volatile.Read(ref status);
            do
            {
                int newValue = WriteValue(value, oldValue, r);
                int currentValue = Interlocked.CompareExchange(ref status, newValue, oldValue);
                if (currentValue == oldValue || ReadValue(currentValue, r) == value)
                {
                    // Change has happened
                    break;
                }
                // Change not done, set old value to current value
                oldValue = currentValue;
            } while (true);
        }
    }

    public bool TrySet(long index, FastBlockStatus newState) => TrySet(index, newState, out _);

    public bool TrySet(long index, FastBlockStatus newState, out FastBlockStatus previousValue)
    {
        if ((ulong)index >= (ulong)_length)
        {
            ThrowIndexOutOfRange();
        }

        FastBlockStatus requiredPriorState = newState switch
        {
            FastBlockStatus.Sent => FastBlockStatus.Pending,
            FastBlockStatus.Inserted => FastBlockStatus.Sent,
            FastBlockStatus.Pending => FastBlockStatus.Sent,
            _ => throw new ArgumentOutOfRangeException(nameof(newState), newState, null)
        };

        (long q, long r) = Math.DivRem(index, 16);
        r *= 2;

        ref int status = ref _statuses[q];
        int oldValue = Volatile.Read(ref status);
        do
        {
            previousValue = ReadValue(oldValue, r);
            if (previousValue != requiredPriorState)
            {
                return false;
            }

            int newValue = WriteValue(newState, oldValue, r);
            int previousValueInt = Interlocked.CompareExchange(ref status, newValue, oldValue);
            if (previousValueInt == oldValue)
            {
                // Change has happened
                return true;
            }

            previousValue = ReadValue(previousValueInt, r);
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
    private static FastBlockStatus ReadValue(int value, long r) => (FastBlockStatus)((value >> (int)r) & 0b11);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteValue(FastBlockStatus newState, int oldValue, long r) => (oldValue & ~(0b11 << (int)r)) | ((int)newState << (int)r);

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowIndexOutOfRange()
    {
        throw new IndexOutOfRangeException();
    }
}
