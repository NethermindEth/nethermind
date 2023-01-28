// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core2.Configuration
{
    public class MaxOperationsPerBlock
    {
        public ulong MaximumAttestations { get; set; }
        public ulong MaximumAttesterSlashings { get; set; }
        public ulong MaximumDeposits { get; set; }
        public ulong MaximumProposerSlashings { get; set; }
        public ulong MaximumVoluntaryExits { get; set; }
    }
}
