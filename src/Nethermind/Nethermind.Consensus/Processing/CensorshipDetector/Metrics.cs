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

    [CounterMetric]
    [Description("Total number of potentially censored blocks.")]
    public static long NumberOfPotentiallyCensoredBlocks;

    [GaugeMetric]
    [Description("Number of last potentially censored block.")]
    public static long LastPotentiallyCensoredBlockNumber;

    [GaugeMetric]
    [Description("Number of last known censored block.")]
    public static long LastCensoredBlockNumber;
}
