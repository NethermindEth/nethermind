// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.StateComposition;

/// <summary>
/// Orchestrates state composition analysis: full trie scans, cached lookups,
/// single-contract inspection, and trie distribution queries. Owns the scan lifecycle.
/// Returns <see cref="Result{T}"/> for business logic outcomes (cooldown, scan in progress);
/// only throws for infrastructure failures.
/// </summary>
public interface IStateCompositionService
{
    /// <summary>
    /// Run a full state composition scan at the given block.
    /// Returns <see cref="Result{T}.Fail"/> if a scan is already in progress or cooldown is active.
    /// </summary>
    Task<Result<StateCompositionStats>> AnalyzeAsync(BlockHeader header, CancellationToken ct);

    /// <summary>
    /// Get trie depth distribution. Returns cached data if available,
    /// or <see cref="Result{T}.Fail"/> if no scan has been completed yet.
    /// </summary>
    Task<Result<TrieDepthDistribution>> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct);

    /// <summary>
    /// Inspect a single contract's storage trie structure.
    /// Returns null data on success if the address has no storage.
    /// Returns <see cref="Result{T}.Fail"/> if another inspection is already running.
    /// </summary>
    Task<Result<TopContractEntry?>> InspectContractAsync(Address address, BlockHeader header, CancellationToken ct);

    /// <summary>
    /// Cancel the currently running scan, if any.
    /// </summary>
    void CancelScan();
}
