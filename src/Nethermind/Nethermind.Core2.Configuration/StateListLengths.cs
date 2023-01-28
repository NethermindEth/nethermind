// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2.Configuration
{
    public class StateListLengths
    {
        public ulong EpochsPerHistoricalVector { get; set; }
        public ulong EpochsPerSlashingsVector { get; set; }
        public ulong HistoricalRootsLimit { get; set; }
        public ulong ValidatorRegistryLimit { get; set; }
    }
}
