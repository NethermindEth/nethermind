// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;

namespace Nethermind.Consensus.Clique
{
    public interface ICliqueConfig : IConfig
    {
        ulong BlockPeriod { get; set; }

        ulong Epoch { get; set; }

        int MinimumOutOfTurnDelay { get; set; }
    }
}
