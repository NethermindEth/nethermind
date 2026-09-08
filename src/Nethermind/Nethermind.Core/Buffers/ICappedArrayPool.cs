// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core.Buffers;

public interface ICappedArrayPool
{
    CappedArray<byte> Rent(int size);

    void Return(in CappedArray<byte> buffer);
}

public static class CappedArrayPoolExtensions
{
    public static CappedArray<byte> SafeRent(this ICappedArrayPool? pool, int size) =>
        pool?.Rent(size) ?? new CappedArray<byte>(new byte[size]);

    public static void SafeReturn(this ICappedArrayPool? pool, in CappedArray<byte> buffer)
    {
        if (pool is not null && buffer.IsNotNull)
        {
            pool.Return(in buffer);
        }
    }
}
