// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Threading;
using Nethermind.Logging;

namespace Nethermind.State.Flat.ScopeProvider;

/// <summary>
/// Shared state for building storage tries in the background while the block is still executing: a bounded budget of
/// concurrent per-contract jobs, the batch threshold above which a contract's pending writes are handed to a job, and
/// the block-level fault and close flags.
/// </summary>
/// <remarks>
/// Each <see cref="FlatStorageTree"/> coalesces its committed writes into a pending map (last value per slot wins) and,
/// once the map holds <see cref="BatchSize"/> entries and a budget slot is free, applies them on a thread-pool job with
/// a bulk set. A contract never has more than one job in flight, so each trie keeps a single writer. Contracts that
/// never reach the threshold take the normal flush path untouched. The scope closes the builder when the write batch
/// starts; from then on no new job starts, and each contract's write batch joins its own job and applies the
/// remaining tail on the flush worker that handles that contract, so the block thread never waits for the whole
/// backlog. A fault poisons the builder; every trie a job touched is then rebuilt from the parent root by the flush.
/// </remarks>
internal sealed class StorageRootBuilder(int concurrency, int batchSize, bool eagerHash, ILogManager logManager)
{
    // The controller counts the calling thread as a slot, hence +1 for exactly `concurrency` concurrent jobs.
    private readonly ConcurrencyController _budget = new(Math.Max(1, concurrency) + 1);
    private readonly ILogger _logger = logManager.GetClassLogger<StorageRootBuilder>();
    private volatile bool _faulted;
    private volatile bool _closed;

    /// <summary>Pending writes a contract accumulates before a background job is started for it.</summary>
    public int BatchSize { get; } = Math.Max(1, batchSize);

    /// <summary>Hash a trie's dirty paths at the end of each job when nothing is pending, so hashing overlaps execution.</summary>
    public bool EagerHash { get; } = eagerHash;

    public bool IsFaulted => _faulted;

    /// <summary>True once the write batch started: no new job may start, tries are finalized by the flush.</summary>
    public bool IsClosed => _closed;

    /// <summary>Test hook invoked on the job thread before each bulk apply.</summary>
    internal static Action? OnBeforeApplyForTests;

    /// <summary>Test hook receiving every fault, so tests can assert the background path ran clean.</summary>
    internal static Action<Exception>? OnFaultForTests;

    public bool TryAcquireJobSlot() => !_closed && !_faulted && _budget.TryRequestConcurrencyQuota();

    public void ReleaseJobSlot() => _budget.ReturnConcurrencyQuota();

    public void Close() => _closed = true;

    public void Fault(Exception e)
    {
        _faulted = true;
        OnFaultForTests?.Invoke(e);
        if (_logger.IsError) _logger.Error("Storage root builder faulted; storage tries will be rebuilt at commit.", e);
    }
}
