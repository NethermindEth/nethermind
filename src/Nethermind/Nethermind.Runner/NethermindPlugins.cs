// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Consensus.AuRa;
using Nethermind.Consensus.Clique;
using Nethermind.Consensus.Ethash;
using Nethermind.EthStats;
using Nethermind.Hive;
using Nethermind.UPnP.Plugin;

namespace Nethermind.Runner;

public static class NethermindPlugins
{
    public static IReadOnlyList<Type> EmbeddedPlugins =
    [
        typeof(AuRaPlugin),
        typeof(CliquePlugin),
        typeof(EthashPlugin),
        typeof(NethDevPlugin),
        typeof(HivePlugin),
        typeof(EthStatsPlugin),
        typeof(Merge.Plugin.MergePlugin),
        typeof(Optimism.OptimismPlugin),
        typeof(Taiko.TaikoPlugin),
        typeof(Nethermind.Flashbots.Flashbots),
        typeof(Merge.AuRa.AuRaMergePlugin),
        typeof(Nethermind.Init.Snapshot.SnapshotPlugin),
        typeof(Shutter.ShutterPlugin),
        typeof(HealthChecks.HealthChecksPlugin),
        typeof(Analytics.AnalyticsPlugin),
        typeof(UPnPPlugin),
    ];
}
