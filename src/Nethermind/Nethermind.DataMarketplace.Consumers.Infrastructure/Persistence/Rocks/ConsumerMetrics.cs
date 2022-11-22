// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Persistence.Rocks
{
    public static class ConsumerMetrics
    {
        public static long ConsumerDepositApprovalsDbReads { get; set; }
        public static long ConsumerDepositApprovalsDbWrites { get; set; }
        public static long ConsumerReceiptsDbReads { get; set; }
        public static long ConsumerReceiptsDbWrites { get; set; }
        public static long ConsumerSessionsDbReads { get; set; }
        public static long ConsumerSessionsDbWrites { get; set; }
        public static long DepositsDbReads { get; set; }
        public static long DepositsDbWrites { get; set; }
    }
}
