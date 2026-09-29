// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Flat.Persistence.TrieNodeLog;

/// <summary>Pass-through <see cref="ITrieNodeLog"/> used when <see cref="Db.TrieNodeLogScope.None"/> is configured.</summary>
public sealed class NullTrieNodeLog : ITrieNodeLog, ITrieNodeLog.IView, ITrieNodeLog.IWriteBatch
{
    public static readonly NullTrieNodeLog Instance = new();

    private NullTrieNodeLog() { }

    public ITrieNodeLog.IView PinLiveGenerations() => this;
    public ITrieNodeLog.IWriteBatch StartWriteBatch(bool bypass) => this;
    public void Drain() { }
    public void Clear() { }

    public void Bind(IReadOnlyKeyValueStore metadata) { }
    public IReadOnlyKeyValueStore Wrap(FlatDbColumns column, IReadOnlyKeyValueStore inner) => inner;

    public IWriteBatch Wrap(FlatDbColumns column, IWriteBatch inner) => inner;
    public void Commit(IWriteOnlyKeyValueStore metadataBatch) { }

    public void Dispose() { }
}
