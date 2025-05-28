// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;

namespace Nethermind.Runner;

public static class NethermindPlugins
{
    public static readonly IReadOnlyList<Type> EmbeddedPlugins =
    [
        typeof(Nethermind.Analytics.AnalyticsPlugin),
        typeof(Nethermind.Consensus.AuRa.AuRaPlugin),
        typeof(Nethermind.Consensus.Clique.CliquePlugin),
        typeof(Nethermind.Consensus.Ethash.EthashPlugin),
        typeof(Nethermind.Consensus.Ethash.NethDevPlugin),
        typeof(Nethermind.EthStats.EthStatsPlugin),
        typeof(Nethermind.Flashbots.Flashbots),
        typeof(Nethermind.HealthChecks.HealthChecksPlugin),
        typeof(Nethermind.Hive.HivePlugin),
        typeof(Nethermind.Init.Snapshot.SnapshotPlugin),
        typeof(Nethermind.Merge.AuRa.AuRaMergePlugin),
        typeof(Nethermind.Merge.Plugin.MergePlugin),
        typeof(Nethermind.Optimism.OptimismPlugin),
        typeof(Nethermind.Shutter.ShutterPlugin),
        typeof(Nethermind.Taiko.TaikoPlugin),
        typeof(Nethermind.UPnP.Plugin.UPnPPlugin),
    ];
}
