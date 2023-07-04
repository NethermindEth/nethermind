// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using Nethermind.Core.Attributes;

namespace Nethermind.AccountAbstraction.Network
{
    public static class Metrics
    {
        [CounterMetric]
        [Description("Number of UserOperations messages received")]
        public static long UserOperationsMessagesReceived { get; set; }

        [CounterMetric]
        [Description("Number of UserOperations messages sent")]
        public static long UserOperationsMessagesSent { get; set; }
    }
}
