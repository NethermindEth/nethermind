// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats.Messages.Models
{
    public class PendingStats(int pending)
    {
        public int Pending { get; } = pending;
    }
}
