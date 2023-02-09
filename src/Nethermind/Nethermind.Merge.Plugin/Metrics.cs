// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Merge.Plugin
{
    public static class Metrics
    {

        [Description("NewPayload request execution time")]
        public static long NewPayloadExecutionTime { get; set; }

        [Description("ForkchoiceUpded request execution time")]
        public static long ForkchoiceUpdedExecutionTime { get; set; }

        [Description("Number of GetPayload Requests")]
        public static long GetPayloadRequests { get; set; }

        [Description("Number of Transactions included in the Last GetPayload Request")]
        public static int NumberOfTransactionsInGetPayload { get; set; }

    }
}
