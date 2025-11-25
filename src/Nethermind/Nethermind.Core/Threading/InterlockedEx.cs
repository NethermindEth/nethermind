// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;

namespace Nethermind.Core.Threading;

public static class InterlockedEx
{
    /// <summary>
    /// Reads location and uses <see cref="mutator"/> with <see cref="target"/> untill successfully changed the location
    /// </summary>
    /// <returns>Result of mutation</returns>
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

    /// <summary>
    /// Atomically sets a field to the maximum of the field's current value and a specified value.
    /// </summary>
    /// <param name="location">The field to update</param>
    /// <param name="value">The value to compare with the current value</param>
    /// <returns>The original value of the field</returns>
    public static int Max(ref int location, int value)
    {
        int current, newValue;
        do
        {
            current = location;
            newValue = Math.Max(current, value);

            // If current is already >= value, no need to update
            if (current >= value)
                return current;
        }
        while (Interlocked.CompareExchange(ref location, newValue, current) != current);

        return current;
    }

    /// <summary>
    /// Atomically sets a field to the maximum of the field's current value and a specified value.
    /// </summary>
    /// <param name="location">The field to update</param>
    /// <param name="value">The value to compare with the current value</param>
    /// <returns>The original value of the field</returns>
    public static long Max(ref long location, long value)
    {
        long current, newValue;
        do
        {
            current = location;
            newValue = Math.Max(current, value);

            // If current is already >= value, no need to update
            if (current >= value)
                return current;
        }
        while (Interlocked.CompareExchange(ref location, newValue, current) != current);

        return current;
    }

    /// <summary>
    /// Atomically sets a field to the minimum of the field's current value and a specified value.
    /// </summary>
    /// <param name="location">The field to update</param>
    /// <param name="value">The value to compare with the current value</param>
    /// <returns>The original value of the field</returns>
    public static int Min(ref int location, int value)
    {
        int current, newValue;
        do
        {
            current = location;
            newValue = Math.Min(current, value);

            // If current is already <= value, no need to update
            if (current <= value)
                return current;
        }
        while (Interlocked.CompareExchange(ref location, newValue, current) != current);

        return current;
    }

    /// <summary>
    /// Atomically sets a field to the minimum of the field's current value and a specified value.
    /// </summary>
    /// <param name="location">The field to update</param>
    /// <param name="value">The value to compare with the current value</param>
    /// <returns>The original value of the field</returns>
    public static long Min(ref long location, long value)
    {
        long current, newValue;
        do
        {
            current = location;
            newValue = Math.Min(current, value);

            // If current is already <= value, no need to update
            if (current <= value)
                return current;
        }
        while (Interlocked.CompareExchange(ref location, newValue, current) != current);

        return current;
    }

}
