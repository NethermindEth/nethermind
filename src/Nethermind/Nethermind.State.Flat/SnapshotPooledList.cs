// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using Nethermind.Core.Collections;

namespace Nethermind.State.Flat;

public sealed class SnapshotPooledList : IDisposable, IEnumerable<Snapshot>
{
    private readonly ArrayPoolList<Snapshot> _list;

    public SnapshotPooledList(int initial)
    {
        _list = new ArrayPoolList<Snapshot>(initial);
    }

    private SnapshotPooledList(ArrayPoolList<Snapshot> list)
    {
        _list = list;
    }

    public int Count => _list.Count;

    public Snapshot this[int index] => _list[index];
    public Snapshot this[Index index] => _list[index];

    public void Add(Snapshot snapshot) => _list.Add(snapshot);

    public void Reverse() => _list.Reverse();

    public static SnapshotPooledList Empty() => new SnapshotPooledList(ArrayPoolList<Snapshot>.Empty());

    public IEnumerator<Snapshot> GetEnumerator() => _list.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        foreach (Snapshot snapshot in _list)
        {
            snapshot.Dispose();
        }

        _list.Dispose();
    }
}
