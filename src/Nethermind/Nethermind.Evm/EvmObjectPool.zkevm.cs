// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Evm;

/// <summary>
/// Object pool for the EVM call machinery, single-threaded guest variant. Same name and shape as the
/// mainline pool, so every call site is unconditional.
/// </summary>
/// <remarks>
/// The guest runs one thread, so the mainline pool's two tiers collapse into one stack: interlocked
/// bookkeeping and segment walking would be pure overhead. The capacity arguments are accepted and
/// ignored - a single thread cannot contend with itself, so there is nothing for the split to buy.
/// </remarks>
internal sealed class EvmObjectPool<T>
{
    private readonly Stack<T> _items = new();

    public EvmObjectPool(int localCapacity = 0, int maxShared = 0)
    {
        // Validated as mainline does, so a bad argument fails the same way in both builds.
        ArgumentOutOfRangeException.ThrowIfNegative(localCapacity);
        ArgumentOutOfRangeException.ThrowIfNegative(maxShared);
    }

    public bool TryDequeue([MaybeNullWhen(false)] out T item) => _items.TryPop(out item);

    public void Enqueue(T item) => _items.Push(item);

    public int Count => _items.Count;
}
