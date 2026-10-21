// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Sockets
{
    public class SocketsMessage(string type, string client, object data)
    {
        public string Type { get; } = type;
        public string Client { get; } = client;
        public object Data { get; } = data;
    }
}
