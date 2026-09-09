// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats.Messages.Models
{
    public class Stats(bool active, bool syncing, bool mining, int hashrate, int peers, long gasPrice, int uptime)
    {
        public bool Active { get; } = active;
        public bool Syncing { get; } = syncing;
        public bool Mining { get; } = mining;
        public int Hashrate { get; } = hashrate;
        public int Peers { get; } = peers;
        public long GasPrice { get; } = gasPrice;
        public int Uptime { get; } = uptime;
    }
}
