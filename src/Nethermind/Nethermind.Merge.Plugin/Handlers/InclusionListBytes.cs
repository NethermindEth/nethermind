// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Merge.Plugin.Handlers;

/// <summary>
/// engine_getInclusionListV1 response: a pool-owned list of pool-owned tx byte buffers.
/// Serializes as a JSON array of hex strings (each inner entry via ArrayPoolListByteHexConverter).
/// Dispose returns every rented buffer — both the outer container and each inner tx-bytes list.
/// </summary>
public sealed class InclusionListBytes(int capacity) : IReadOnlyList<ArrayPoolList<byte>>, IDisposable
{
    private readonly ArrayPoolList<ArrayPoolList<byte>> _items = new(capacity);

    public void Add(ArrayPoolList<byte> item) => _items.Add(item);

    public ArrayPoolList<byte> this[int index] => _items[index];
    public int Count => _items.Count;
    public IEnumerator<ArrayPoolList<byte>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _items.DisposeRecursive();
}
