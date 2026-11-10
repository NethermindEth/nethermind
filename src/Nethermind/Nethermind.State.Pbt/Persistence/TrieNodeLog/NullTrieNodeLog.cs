// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;

namespace Nethermind.State.Pbt.Persistence.TrieNodeLog;

/// <summary>Pass-through <see cref="ITrieNodeLog"/> used when the trie node log is disabled.</summary>
public sealed class NullTrieNodeLog : ITrieNodeLog, ITrieNodeLog.IWriteBatch
{
    public static readonly NullTrieNodeLog Instance = new();

    private NullTrieNodeLog() { }

    public ITrieNodeLog.IView OpenView(IColumnsDb<PbtColumns> db) => new View(db.CreateSnapshot());
    public ITrieNodeLog.IWriteBatch StartWriteBatch(IColumnsWriteBatch<PbtColumns> batch) => this;
    public void Drain() { }

    public IWriteBatch Wrap(PbtColumns column, IWriteBatch inner) => inner;
    public void Commit() { }
    public void Confirm() { }

    public void Dispose() { }

    private sealed class View(IColumnDbSnapshot<PbtColumns> snapshot) : ITrieNodeLog.IView
    {
        public IColumnDbSnapshot<PbtColumns> Snapshot => snapshot;
        public IReadOnlyKeyValueStore GetColumn(PbtColumns column) => snapshot.GetColumn(column);
        public void Dispose() => snapshot.Dispose();
    }
}
