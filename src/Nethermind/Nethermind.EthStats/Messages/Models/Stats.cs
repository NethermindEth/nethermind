// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats.Messages.Models
{
    public class Stats
    {
        public bool Active { get; }
        public bool Syncing { get; }
        public bool Mining { get; }
        public int Hashrate { get; }
        public int Peers { get; }
        public long GasPrice { get; }
        public int Uptime { get; }

        public Stats(bool active, bool syncing, bool mining, int hashrate, int peers, long gasPrice, int uptime)
        {
            Active = active;
            Syncing = syncing;
            Mining = mining;
            Hashrate = hashrate;
            Peers = peers;
            GasPrice = gasPrice;
            Uptime = uptime;
        }
    }
}
