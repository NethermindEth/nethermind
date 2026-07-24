// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Merge.Plugin.Handlers;

public sealed class InclusionListBytes(int capacity) : IReadOnlyList<ArrayPoolList<byte>>, IDisposable
{
    // JsonRpc dispose chain: JsonRpcResponse → ResultWrapper.TryDispose → InclusionListBytes.Dispose → DisposeRecursive.
    private readonly ArrayPoolList<ArrayPoolList<byte>> _items = new(capacity);

    public void Add(ArrayPoolList<byte> item) => _items.Add(item);

    public ArrayPoolList<byte> this[int index] => _items[index];
    public int Count => _items.Count;
    public IEnumerator<ArrayPoolList<byte>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _items.DisposeRecursive();
}
