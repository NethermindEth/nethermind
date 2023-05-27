// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

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

    public FastBlockStatus this[long index]
    {
        get
        {
            if ((ulong)index >= (ulong)_length)
            {
                ThrowIndexOutOfRange();
            }

            (long q, long r) = Math.DivRem(index, 16);

            int status = Volatile.Read(ref _statuses[q]);
            return (FastBlockStatus)((status >> (int)(r * 2)) & 0b11);
        }
        set
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
                int newValue = (int)((oldValue & ~(0b11 << (int)r)) | ((int)value << (int)r));
                int currentValue = Interlocked.CompareExchange(ref status, newValue, oldValue);
                if (currentValue == oldValue || currentValue == newValue)
                {
                    // Change has happened
                    break;
                }
                // Change not done, set old value to current value
                oldValue = currentValue;
            } while (true);
        }
    }

    [DoesNotReturn]
    [StackTraceHidden]
    private static void ThrowIndexOutOfRange()
    {
        throw new IndexOutOfRangeException();
    }
}
