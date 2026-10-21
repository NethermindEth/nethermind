// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EraE.Proofs;

public sealed class NullHistoricalSummariesProvider : IHistoricalSummariesProvider
{
    public static readonly NullHistoricalSummariesProvider Instance = new();

    private NullHistoricalSummariesProvider() { }

    public Task<HistoricalSummary?> GetHistoricalSummary(int index, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult<HistoricalSummary?>(null);

    public Task<HistoricalSummary[]> GetHistoricalSummariesAsync(string stateId = "head", bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult(Array.Empty<HistoricalSummary>());

    public void Dispose() { }
}
