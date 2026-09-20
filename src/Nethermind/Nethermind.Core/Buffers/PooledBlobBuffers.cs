// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Threading;

namespace Nethermind.Core.Buffers;

/// <summary>
/// Tracks exclusively owned blob buffers until a received transaction is published.
/// </summary>
internal sealed class PooledBlobBuffers(byte[][] buffers)
{
    // EIP-4844: 4096 field elements of 32 bytes. Retain at most 4 MiB.
    internal const int BlobSize = 4096 * 32;
    private static readonly ArrayPool<byte> Pool = ArrayPool<byte>.Create(BlobSize, 32);
    private byte[][]? _buffers = buffers;

    internal static byte[] Copy(ReadOnlySpan<byte> source)
    {
        if (source.Length != BlobSize) return source.ToArray();
        byte[] buffer = Pool.Rent(BlobSize);
        source.CopyTo(buffer);
        return buffer;
    }

    internal void Disown() => Interlocked.Exchange(ref _buffers, null);

    internal bool Return()
    {
        byte[][]? owned = Interlocked.Exchange(ref _buffers, null);
        if (owned is null) return false;
        foreach (byte[]? buffer in owned)
        {
            if (buffer is { Length: BlobSize }) Pool.Return(buffer);
        }
        return true;
    }

    internal static void Return(Transaction transaction)
    {
        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper
            && wrapper.PooledBuffers?.Return() == true)
        {
            transaction.NetworkWrapper = null;
        }
    }

    internal static void Disown(Transaction transaction)
    {
        if (transaction.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
            wrapper.PooledBuffers?.Disown();
    }
}
