// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

public static class InterlockedEx
{
    public static int MutateValue(ref int location, int target, Func<int, int, int> mutator)
    {
        int locationValue;
        do
        {
            locationValue = Volatile.Read(ref location);
            target = mutator(locationValue, target);
        } while (Interlocked.CompareExchange(ref location, target, locationValue) != locationValue);

        return target;
    }
}
