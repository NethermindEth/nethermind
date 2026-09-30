// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Collections;

namespace Nethermind.State.Pbt;

/// <summary>
/// An owned, pool-backed list of leased <see cref="PbtSnapshot"/> layers.
/// </summary>
/// <remarks>
/// <see cref="Dispose"/> releases one lease per element and returns the backing array to the pool, so
/// every list must be owned by exactly one holder — a <c>using</c> at the construction site until it
/// is handed to a bundle, and the bundle afterwards. There is deliberately no <c>Clear</c>: the
/// underlying reset drops the count without releasing the element leases.
/// <para>
/// Not thread-safe even for readers: <see cref="Add"/> returns the old array to the pool when it
/// grows, so a read racing a growth can touch a pool-returned array.
/// </para>
/// </remarks>
public sealed class PbtSnapshotPooledList(int initialCapacity) : IDisposable, IReadOnlyList<PbtSnapshot>
{
    private readonly ArrayPoolList<PbtSnapshot> _list = new(initialCapacity);

    public int Count => _list.Count;

    public PbtSnapshot this[int index] => _list[index];

    public void Add(PbtSnapshot snapshot) => _list.Add(snapshot);

    public void Reverse() => _list.Reverse();

    public IEnumerator<PbtSnapshot> GetEnumerator() => _list.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose() => _list.DisposeRecursive();
}
