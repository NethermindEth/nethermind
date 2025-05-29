// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;


public interface ISpanSourcePool
{
    SpanSource Rent(int size);

    void Return(SpanSource buffer);
}

public static class SpanSourcePoolExtensions
{
    public static SpanSource SafeRentBuffer(this ISpanSourcePool? pool, int size)
    {
        if (size <= TinyArray.MaxLength)
        {
            return new SpanSource(TinyArray.Create(size));
        }

        if (pool is null)
        {
            return new SpanSource(new byte[size]);
        }

        return pool.Rent(size);
    }

    public static void SafeReturnBuffer(this ISpanSourcePool? pool, in SpanSource buffer)
    {
        pool?.Return(buffer);
    }
}
