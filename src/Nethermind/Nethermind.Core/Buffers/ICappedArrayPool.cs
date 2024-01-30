// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;

public interface ICappedArrayPool
{
    CappedArray<byte> Rent(int size);

    void Return(in CappedArray<byte> buffer);
}

public static class BufferPoolExtensions
{
    public static CappedArray<byte> SafeRentBuffer(this ICappedArrayPool? pool, int size)
    {
        if (pool is null) return new CappedArray<byte>(new byte[size]);
        return pool.Rent(size);
    }

    public static void SafeReturnBuffer(this ICappedArrayPool? pool, in CappedArray<byte> buffer)
    {
        pool?.Return(buffer);
    }
}
