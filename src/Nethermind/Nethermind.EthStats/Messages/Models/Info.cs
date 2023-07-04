// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
