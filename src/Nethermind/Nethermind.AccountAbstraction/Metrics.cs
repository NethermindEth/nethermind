// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.AccountAbstraction
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Total number of UserOperation objects received for inclusion")]
        public static int UserOperationsReceived { get; set; }

        [CounterMetric]
        [Description("Total number of UserOperation objects simulated")]
        public static int UserOperationsSimulated { get; set; }

        [CounterMetric]
        [Description("Total number of UserOperation objects accepted into the pool")]
        public static int UserOperationsPending { get; set; }

        [CounterMetric]
        [Description("Total number of UserOperation objects included into the chain by this miner")]
        public static int UserOperationsIncluded { get; set; }
    }
}
