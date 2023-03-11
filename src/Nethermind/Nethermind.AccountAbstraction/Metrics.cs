// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;

namespace Nethermind.AccountAbstraction
{
    public static class Metrics
    {
        [Description("Total number of UserOperation objects received for inclusion")]
        public static int UserOperationsReceived { get; set; } = 0;

        [Description("Total number of UserOperation objects simulated")]
        public static int UserOperationsSimulated { get; set; } = 0;

        [Description("Total number of UserOperation objects accepted into the pool")]
        public static int UserOperationsPending { get; set; } = 0;

        [Description("Total number of UserOperation objects included into the chain by this miner")]
        public static int UserOperationsIncluded { get; set; } = 0;
    }
}
