// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>Pass-through <see cref="ITrieNodeLog"/> used when the trie node log is disabled.</summary>
public sealed class NullTrieNodeLog : ITrieNodeLog, ITrieNodeLog.IWriteBatch
{
    public static readonly NullTrieNodeLog Instance = new();

    private NullTrieNodeLog() { }

    public ITrieNodeLog.IView OpenView(IColumnsDb<FlatDbColumns> db, ReaderFlags flags) => new View(db.CreateSnapshot(flags));
    public ITrieNodeLog.IWriteBatch StartWriteBatch(IColumnsWriteBatch<FlatDbColumns> batch, bool bypass) => this;
    public void Drain() { }
    public void Clear() { }

    public IWriteBatch Wrap(FlatDbColumns column, IWriteBatch inner) => inner;
    public void Commit() { }

    public void Dispose() { }

    private sealed class View(IColumnDbSnapshot<FlatDbColumns> snapshot) : ITrieNodeLog.IView
    {
        public IColumnDbSnapshot<FlatDbColumns> Snapshot => snapshot;
        public IReadOnlyKeyValueStore GetColumn(FlatDbColumns column) => snapshot.GetColumn(column);
        public void Dispose() => snapshot.Dispose();
    }
}
