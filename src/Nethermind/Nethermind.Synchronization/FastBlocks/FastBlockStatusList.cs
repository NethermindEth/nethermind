// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
namespace Nethermind.Synchronization.FastBlocks;

internal class FastBlockStatusList
{
    private readonly byte[] _statuses;
    private readonly long _length;

    public FastBlockStatusList(long length)
    {
        // Can fit 4 statuses per byte, however need to round up the division
        long size = length / 4;
        if (size * 4 < length)
        {
            size++;
        }

        _statuses = new byte[size];
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

            (long q, long r) = Math.DivRem(index, 4);

            byte status = _statuses[q];
            return (FastBlockStatus)((status >> (int)(r * 2)) & 0b11);
        }
        set
        {
            if ((ulong)index >= (ulong)_length)
            {
                ThrowIndexOutOfRange();
            }

            (long q, long r) = Math.DivRem(index, 4);
            r *= 2;

            ref byte status = ref _statuses[q];
            status = (byte)((status & ~(0b11 << (int)r)) | ((int)value << (int)r));
        }
    }

    private static void ThrowIndexOutOfRange()
    {
        throw new IndexOutOfRangeException();
    }
}
