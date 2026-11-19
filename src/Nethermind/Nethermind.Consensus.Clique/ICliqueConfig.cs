// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.Clique
{
    public interface ICliqueConfig : IConfig
    {
        [ConfigItem(Description = "The block period, in seconds. Always taken from the chain spec.", DefaultValue = "15", HiddenFromDocs = true)]
        ulong BlockPeriod { get; set; }

        [ConfigItem(Description = "The epoch length, in blocks. Always taken from the chain spec.", DefaultValue = "30000", HiddenFromDocs = true)]
        ulong Epoch { get; set; }

        [ConfigItem(Description = "The minimum delay, in milliseconds, before the node seals a block out of turn.", DefaultValue = "0")]
        int MinimumOutOfTurnDelay { get; set; }
    }
}
