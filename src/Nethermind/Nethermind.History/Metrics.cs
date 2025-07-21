// SPDX-FileCopyrightText: 20225Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.History;

public static class Metrics
{
    [GaugeMetric]
    [Description("The number of the oldest block stored.")]
    public static long OldestStoredBlockNumber { get; set; }
}
