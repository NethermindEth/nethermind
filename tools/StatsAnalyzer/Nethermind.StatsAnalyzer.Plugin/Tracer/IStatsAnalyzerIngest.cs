// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.StatsAnalyzer.Plugin.Tracer;

/// <summary>
/// Feeds a tracer's recorded buffer into its analyzer without building a report.
/// </summary>
internal interface IStatsAnalyzerIngest
{
    void Ingest();
}
