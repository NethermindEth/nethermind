// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.AccountAbstraction.Network
{
    public static class Metrics
    {
        [Description("Number of UserOperations messages received")]
        public static long UserOperationsMessagesReceived { get; set; }

        [Description("Number of UserOperations messages sent")]
        public static long UserOperationsMessagesSent { get; set; }
    }
}
