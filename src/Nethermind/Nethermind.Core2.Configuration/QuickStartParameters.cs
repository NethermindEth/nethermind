// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Configuration
{
    public class QuickStartParameters
    {
        public Bytes32 Eth1BlockHash { get; set; } = Bytes32.Zero;
        /// <summary>
        /// Eth1Timestamp must be valid for the given Quickstart Genesis time (between MinimumGenesisDelay and 2 * MinimumGenesisDelay before genesis)
        /// </summary>
        public ulong Eth1Timestamp { get; set; }
        public ulong GenesisTime { get; set; }
        public bool UseSystemClock { get; set; }
        public ulong ValidatorCount { get; set; }
        public long ClockOffset { get; set; }
        public ulong ValidatorStartIndex { get; set; }
        public ulong NumberOfValidators { get; set; }
    }
}
