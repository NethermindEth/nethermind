// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.Core.Collections;

/// <summary>
/// Shared array pool. Standard execution delegates small rents to <see cref="ArrayPool{T}.Shared"/>;
/// rents above 1 MiB go to a dedicated non-trimming pool — the shared pool drops its retained
/// arrays on gen2/high-memory-pressure callbacks, so at large state every whole-block-sized buffer
/// (block RLP, JSON-RPC payload bodies) became a fresh large-object-heap allocation feeding
/// blocking OutOfSpaceLOH collections. The zkVM build provides a single-threaded power-of-two
/// bucket pool instead.
/// </summary>
public static class SafeArrayPool<T>
{
    public static readonly ArrayPool<T> Shared = new TieredPool();

    private sealed class TieredPool : ArrayPool<T>
    {
        private const int LargeThreshold = 1 << 20;

        private readonly ArrayPool<T> _large = Create(1 << 27, 8);

        public override T[] Rent(int minimumLength) =>
            minimumLength <= LargeThreshold ? ArrayPool<T>.Shared.Rent(minimumLength) : _large.Rent(minimumLength);

        public override void Return(T[] array, bool clearArray = false)
        {
            if (array.Length <= LargeThreshold) ArrayPool<T>.Shared.Return(array, clearArray);
            else _large.Return(array, clearArray);
        }
    }
}
