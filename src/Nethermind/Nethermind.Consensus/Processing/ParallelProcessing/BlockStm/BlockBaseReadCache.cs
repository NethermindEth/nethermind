// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;

namespace Nethermind.Consensus.Processing.ParallelProcessing.BlockStm;

/// <summary>
/// Per-block read-through cache for the base (pre-block) world state, shared across all
/// Block-STM workers. Backs the <c>Status.NotFound</c> fall-through inside
/// <see cref="MultiVersionMemoryScopeProvider"/> so workers reading the same base account
/// or storage slot don't each pay the trie / flat-snapshot lookup.
/// </summary>
/// <remarks>
/// Correctness rests on one invariant: the resettable base scope is read-only for the
/// duration of the block. Every in-block write goes through <c>MultiVersionMemoryWriteBatch</c>
/// into MVMM's data dictionary; <c>baseScope.StartWriteBatch</c> is never called by the
/// parallel path. So every cached value is the pre-block value of that location, identical
/// for every worker that observes it.
///
/// Races on first read are harmless: two workers reading the same uncached slot both
/// compute the same base value, and <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/>
/// keeps whichever arrives first. The duplicate read costs are exactly the cost we would
/// have paid without the cache; subsequent reads of either worker hit.
///
/// Lifetime is one block — created in
/// <c>BlockStmTransactionsExecutor.ProcessTransactions</c> and dropped when the method
/// returns. The cache is never persisted across blocks.
/// </remarks>
public sealed class BlockBaseReadCache
{
    private readonly ConcurrentDictionary<AddressAsKey, Account?> _accounts = new();
    private readonly ConcurrentDictionary<StorageCell, byte[]> _storage = new();

    public bool TryGetAccount(Address address, out Account? account) =>
        _accounts.TryGetValue(address, out account);

    public void SetAccount(Address address, Account? account) =>
        _accounts.TryAdd(address, account);

    public bool TryGetStorage(in StorageCell cell, out byte[] value) =>
        _storage.TryGetValue(cell, out value!);

    public void SetStorage(in StorageCell cell, byte[] value) =>
        _storage.TryAdd(cell, value);
}
