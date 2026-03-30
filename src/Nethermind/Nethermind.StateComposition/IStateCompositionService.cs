// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.StateComposition;

/// <summary>
/// Orchestrates state composition analysis: full trie scans, cached lookups,
/// and trie distribution queries. Owns the scan lifecycle.
/// </summary>
public interface IStateCompositionService
{
    /// <summary>
    /// Run a full state composition scan at the given block.
    /// Fails fast if a scan is already in progress.
    /// </summary>
    Task<StateCompositionStats> AnalyzeAsync(BlockHeader header, CancellationToken ct);

    /// <summary>
    /// Get trie depth distribution. Returns cached data if available,
    /// otherwise triggers a scan.
    /// </summary>
    Task<TrieDepthDistribution> GetTrieDistributionAsync(BlockHeader header, CancellationToken ct);
}
