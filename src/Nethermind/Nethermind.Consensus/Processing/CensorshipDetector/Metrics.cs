// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus.Processing.CensorshipDetector;

public static class Metrics
{
    [CounterMetric]
    [Description("Total number of censored blocks.")]
    public static long NumberOfCensoredBlocks;

    [GaugeMetric]
    [Description("Number of last known censored block.")]
    public static long LastCensoredBlockNumber;

    [CounterMetric]
    [Description("Number of unique addresses specified by the user for censorship detection, to which txs are sent currently in the pool.")]
    public static long PoolCensorshipDetectionUniqueAddressesCount;
}
