// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats.Messages.Models
{
    public class PendingStats
    {
        public int Pending { get; }

        public PendingStats(int pending)
        {
            Pending = pending;
        }
    }
}
