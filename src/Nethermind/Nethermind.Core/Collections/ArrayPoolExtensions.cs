// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Core.Collections;

public static class ArrayPoolExtensions
{
    /// <summary>
    /// Rent an array exactly of the same size. May return unpooled array if ArrayPool can't return exact size.
    /// Always use ReturnExact, or it may throw exception. Don't use this for large array that is probably
    /// not power of two size.
    /// </summary>
    /// <param name="pool"></param>
    /// <param name="length"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T[] RentExact<T>(this ArrayPool<T> pool, int length)
    {
        if (!ProbablyCanPool(length))
        {
            return new T[length];
        }

        T[] pooled = pool.Rent(length);
        if (pooled.Length != length)
        {
            // Hmm.. check missed for some reason. Different ArrayPool implementation?
            pool.Return(pooled);
            return new T[length];
        }

        return pooled;
    }

    /// <summary>
    /// Safely return an array even if its probably not pooled and can't be pooled.
    /// </summary>
    /// <param name="pool"></param>
    /// <param name="array"></param>
    /// <typeparam name="T"></typeparam>
    public static void ReturnExact<T>(this ArrayPool<T> pool, T[] array)
    {
        if (!ProbablyCanPool(array.Length))
        {
            return;
        }

        try
        {
            pool.Return(array);
        }
        catch (ArgumentException)
        {
        }
    }

    /// <summary>
    /// Based on the source code of `System.Buffers.Utilities.SelectBucketIndex`, which is used by `ConfigurableArrayPool`.
    /// and the `TlsOverPerCoreLockedStacksArrayPool`, which is the default shared one.
    /// </summary>
    /// <param name="length"></param>
    /// <returns></returns>
    static bool ProbablyCanPool(int length)
    {
        return length >= 16 && IsPowerOfTwo(length);
    }

    static bool IsPowerOfTwo(int x)
    {
        return (x & (x - 1)) == 0;
    }
}
