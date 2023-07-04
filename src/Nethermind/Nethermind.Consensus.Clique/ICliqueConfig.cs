// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Consensus.Clique
{
    public interface ICliqueConfig
    {
        ulong BlockPeriod { get; set; }

        ulong Epoch { get; set; }
    }
}
