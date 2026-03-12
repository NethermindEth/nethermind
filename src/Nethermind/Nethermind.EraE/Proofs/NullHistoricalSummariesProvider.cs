// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Proofs;

/// <summary>
/// A no-op historical summaries provider that always returns null / empty.
/// Used when a beacon node is unavailable (import/verification only, no HistoricalSummaries proof generation).
/// </summary>
public sealed class NullHistoricalSummariesProvider : IHistoricalSummariesProvider
{
    public static readonly NullHistoricalSummariesProvider Instance = new();

    private NullHistoricalSummariesProvider() { }

    public Task<HistoricalSummary?> GetHistoricalSummary(int index, CancellationToken cancellationToken = default, bool forceRefresh = false) =>
        Task.FromResult<HistoricalSummary?>(null);

    public Task<HistoricalSummary[]> GetHistoricalSummariesAsync(string stateId = "head", CancellationToken cancellationToken = default, bool forceRefresh = false) =>
        Task.FromResult(Array.Empty<HistoricalSummary>());

    public void Dispose() { }
}
