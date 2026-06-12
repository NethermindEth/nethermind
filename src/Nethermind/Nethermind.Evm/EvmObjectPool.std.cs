// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// Object pool used by the EVM call machinery. Mainline build wraps
/// <see cref="ConcurrentQueue{T}"/> so concurrent EVM execution stays lock-free.
/// </summary>
internal sealed class EvmObjectPool<T>
{
    private readonly ConcurrentQueue<T> _items = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item) => _items.TryDequeue(out item!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(T item) => _items.Enqueue(item);

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _items.Count;
    }
}
