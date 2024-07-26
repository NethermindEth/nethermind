// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.Merge.Plugin
{
    public static class Metrics
    {
        [GaugeMetric]
        [Description("NewPayload request execution time")]
        public static long NewPayloadExecutionTime { get; set; }

        [GaugeMetric]
        [Description("ForkchoiceUpded request execution time")]
        public static long ForkchoiceUpdedExecutionTime { get; set; }

        [CounterMetric]
        [Description("Number of GetPayload Requests")]
        public static long GetPayloadRequests { get; set; }

        [GaugeMetric]
        [Description("Number of Transactions included in the Last GetPayload Request")]
        public static int NumberOfTransactionsInGetPayload { get; set; }
    }
}
