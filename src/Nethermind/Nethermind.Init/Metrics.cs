// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Init
{
    public static class Metrics
    {
        [Description("Version number")]
        public static long Version { get; set; }

        [CounterMetric]
        [Description("Rejected blocks that were valid when re-executed for their diagnostics dump")]
        public static long InvalidBlocksValidOnRerun { get; set; }
    }
}
