// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Trie.Pruning;

public interface IBufferPool
{
    CappedArray<byte> RentBuffer(int size) => new CappedArray<byte>(new byte[size]);

    void ReturnBuffer(CappedArray<byte> buffer)
    {
    }
}

public static class BufferPoolExtensions
{
    public static CappedArray<byte> SafeRentBuffer(this IBufferPool? pool, int size)
    {
        if (pool == null) return new CappedArray<byte>(new byte[size]);
        CappedArray<byte> returnedBuffer = pool.RentBuffer(size);
        if (returnedBuffer.Array == null)
        {
            return new CappedArray<byte>(new byte[size]);
        }

        return returnedBuffer;
    }

    public static void SafeReturnBuffer(this IBufferPool? pool, CappedArray<byte> buffer)
    {
        pool?.ReturnBuffer(buffer);
    }
}
