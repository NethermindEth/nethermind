//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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
