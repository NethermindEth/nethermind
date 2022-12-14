// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Consumers
{
    public static class Metrics
    {
        public static long SentQueries { get; set; }
        public static long ConsumedUnits { get; set; }
        public static long ReceivedData { get; set; }
    }
}
