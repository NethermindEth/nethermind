// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.EthStats.Messages.Models
{
    public class Info(string name, string node, int port, string net, string protocol, string api, string os, string osV,
        string client, string contact, bool canUpdateHistory)
    {
        public string Name { get; } = name;
        public string Node { get; } = node;
        public int Port { get; } = port;
        public string Net { get; } = net;
        public string Protocol { get; } = protocol;
        public string Api { get; } = api;
        public string Os { get; } = os;
        public string Os_V { get; } = osV;
        public string Client { get; } = client;
        public string Contact { get; } = contact;
        public bool CanUpdateHistory { get; } = canUpdateHistory;
    }
}
