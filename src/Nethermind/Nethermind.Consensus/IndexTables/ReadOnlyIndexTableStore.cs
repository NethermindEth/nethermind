// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.IndexTables;

/// <summary>
/// Serves the node's EIP-8304 index tables while discarding every table a re-execution produces.
/// </summary>
/// <remarks>
/// Tracing and other read-only environments re-execute blocks, possibly with altered transactions, under their
/// canonical hashes, so their tables must never reach the node's store. Nothing is cached locally either: a pooled
/// environment would otherwise serve one call's tables to the next.
/// </remarks>
public sealed class ReadOnlyIndexTableStore(IIndexTableStore baseStore) : IIndexTableStore
{
    /// <inheritdoc />
    public void Store(int level, long firstBlock, IReadOnlyList<IndexEntry> sortedEntries, Hash256? blockHash = null) { }

    /// <inheritdoc />
    public IReadOnlyList<IndexEntry>? Get(int level, long firstBlock, Hash256? blockHash = null) =>
        baseStore.Get(level, firstBlock, blockHash);

    /// <inheritdoc />
    public void Remove(int level, long firstBlock, Hash256? blockHash = null) { }
}
