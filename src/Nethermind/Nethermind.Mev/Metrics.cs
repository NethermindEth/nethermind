// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;
using Nethermind.Int256;

namespace Nethermind.Mev
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Total number of bundles received for inclusion")]
        public static int BundlesReceived { get; set; }

        [CounterMetric]
        [Description("Total number of valid bundles received for inclusion")]
        public static int ValidBundlesReceived { get; set; }

        [CounterMetric]
        [Description("Total number of megabundles received for inclusion")]
        public static int MegabundlesReceived { get; set; }

        [CounterMetric]
        [Description("Total number of valid megabundles received for inclusion")]
        public static int ValidMegabundlesReceived { get; set; }

        [CounterMetric]
        [Description("Total number of bundles simulated")]
        public static int BundlesSimulated { get; set; }

        [GaugeMetric]
        [Description("Total coinbase payments in wei")]
        public static decimal TotalCoinbasePayments { get; set; }
    }
}
