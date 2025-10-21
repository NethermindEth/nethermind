// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;

public static class SpanSourcePoolExtensions
{
    public static SpanSource SafeRentBuffer(this ICappedArrayPool? pool, int size)
    {
        if (pool is null)
        {
            return new SpanSource(new byte[size]);
        }

        return new SpanSource(pool.Rent(size));
    }

    public static void SafeReturnBuffer(this ICappedArrayPool? pool, SpanSource buffer)
    {
        if (pool is not null && buffer.TryGetCappedArray(out CappedArray<byte> capped))
        {
            pool.Return(capped);
        }
    }
}
