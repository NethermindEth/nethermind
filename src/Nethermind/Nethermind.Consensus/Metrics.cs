// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Consensus;

public class Metrics
{
    [GaugeMetric]
    [Description("The number of tasks scheduled in the background.")]
    public static long NumberOfBackgroundTasksScheduled { get; set; }
}
