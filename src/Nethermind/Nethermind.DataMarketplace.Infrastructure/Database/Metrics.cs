// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.DataMarketplace.Infrastructure.Database
{
    public static class Metrics
    {
        [Description("Number of Eth Request (faucet) DB reads.")]
        public static long EthRequestsDbReads { get; set; }

        [Description("Number of Eth Request (faucet) DB writes.")]
        public static long EthRequestsDbWrites { get; set; }

        [Description("Number of configs DB reads.")]
        public static long ConfigsDbReads { get; set; }

        [Description("Number of configs DB writes.")]
        public static long ConfigsDbWrites { get; set; }
    }
}
