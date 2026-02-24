// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// A simple disposable list of persisted snapshots, ordered oldest-first.
/// Domain-specific query logic lives in <see cref="ReadOnlySnapshotBundle"/>.
/// </summary>
public sealed class PersistedSnapshotList : IDisposable, IEnumerable<PersistedSnapshot>
{
    private readonly ArrayPoolList<PersistedSnapshot> _list;

    public PersistedSnapshotList(int initial)
    {
        _list = new ArrayPoolList<PersistedSnapshot>(initial);
    }

    private PersistedSnapshotList(ArrayPoolList<PersistedSnapshot> list)
    {
        _list = list;
    }

    public int Count => _list.Count;

    public PersistedSnapshot this[int index] => _list[index];
    public PersistedSnapshot this[Index index] => _list[index];

    public void Add(PersistedSnapshot snapshot) => _list.Add(snapshot);

    public void Reverse() => _list.Reverse();

    public static PersistedSnapshotList Empty() => new PersistedSnapshotList(ArrayPoolList<PersistedSnapshot>.Empty());

    public IEnumerator<PersistedSnapshot> GetEnumerator() => _list.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        foreach (PersistedSnapshot snapshot in _list)
        {
            snapshot.Dispose();
        }

        _list.Dispose();
    }
}
