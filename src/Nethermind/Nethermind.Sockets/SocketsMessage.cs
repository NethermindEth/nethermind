// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Sockets
{
    public class SocketsMessage
    {
        public string Type { get; }
        public string Client { get; }
        public object Data { get; }

        public SocketsMessage(string type, string client, object data)
        {
            Type = type;
            Client = client;
            Data = data;
        }
    }
}
