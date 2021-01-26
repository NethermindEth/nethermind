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
    public class Info
    {
        public string Name { get; }
        public string Node { get; }
        public int Port { get; }
        public string Net { get; }
        public string Protocol { get; }
        public string Api { get; }
        public string Os { get; }
        public string Os_V { get; }
        public string Client { get; }
        public string Contact { get; }
        public bool CanUpdateHistory { get; }

        public Info(string name, string node, int port, string net, string protocol, string api, string os, string osV,
            string client, string contact, bool canUpdateHistory)
        {
            Name = name;
            Node = node;
            Port = port;
            Net = net;
            Protocol = protocol;
            Api = api;
            Os = os;
            Os_V = osV;
            Client = client;
            Contact = contact;
            CanUpdateHistory = canUpdateHistory;
        }
    }
}
