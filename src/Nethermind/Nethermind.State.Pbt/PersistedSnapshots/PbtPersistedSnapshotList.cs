// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Collections;

namespace Nethermind.State.Pbt.PersistedSnapshots;

/// <summary>
/// An owned, pool-backed list of leased <see cref="PbtPersistedSnapshot"/> layers, oldest first.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> releases one lease per element and returns the backing array to the pool.
/// </remarks>
public sealed class PbtPersistedSnapshotList(int initialCapacity) : IDisposable, IReadOnlyList<PbtPersistedSnapshot>
{
    private readonly ArrayPoolList<PbtPersistedSnapshot> _list = new(initialCapacity);

    public int Count => _list.Count;

    public PbtPersistedSnapshot this[int index] => _list[index];

    public void Add(PbtPersistedSnapshot snapshot) => _list.Add(snapshot);

    public IEnumerator<PbtPersistedSnapshot> GetEnumerator() => _list.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _list.DisposeRecursive();
}
