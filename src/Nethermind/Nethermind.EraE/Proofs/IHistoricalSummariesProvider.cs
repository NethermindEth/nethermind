// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.EraE.Proofs;

public readonly struct HistoricalSummary(ValueHash256 blockSummaryRoot, ValueHash256 stateSummaryRoot)
{
    public ValueHash256 BlockSummaryRoot { get; } = blockSummaryRoot;
    public ValueHash256 StateSummaryRoot { get; } = stateSummaryRoot;

    public static HistoricalSummary From(string blockSummaryRootHex, string stateSummaryRootHex) =>
        new(new ValueHash256(blockSummaryRootHex), new ValueHash256(stateSummaryRootHex));
}

public interface IHistoricalSummariesProvider : IDisposable
{
    Task<HistoricalSummary?> GetHistoricalSummary(int index, bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task<HistoricalSummary[]> GetHistoricalSummariesAsync(string stateId = "head", bool forceRefresh = false, CancellationToken cancellationToken = default);
}
