// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Buffers;

namespace Nethermind.Trie.Pruning;

public interface ICappedArrayPool
{
    CappedArray<byte> Rent(int size);

    void Return(CappedArray<byte> buffer);
}

public static class BufferPoolExtensions
{
    public static CappedArray<byte> SafeRentBuffer(this ICappedArrayPool? pool, int size)
    {
        if (pool == null) return new CappedArray<byte>(new byte[size]);
        CappedArray<byte> returnedBuffer = pool.Rent(size);
        if (returnedBuffer.Array == null)
        {
            // Used in unit testing where pool is an nsubstitute
            return new CappedArray<byte>(new byte[size]);
        }

        return returnedBuffer;
    }

    public static void SafeReturnBuffer(this ICappedArrayPool? pool, CappedArray<byte> buffer)
    {
        pool?.Return(buffer);
    }
}
