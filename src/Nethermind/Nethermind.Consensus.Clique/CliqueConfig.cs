// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Clique
{
    public class CliqueConfig : ICliqueConfig
    {
        public static ICliqueConfig Default = new CliqueConfig();

        public ulong BlockPeriod { get; set; } = 15;

        public ulong Epoch { get; set; } = 30000;
    }
}
