// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;

namespace Nethermind.Sockets
{
    public class WebSocketsReceiveResult : ReceiveResult
    {
        public WebSocketCloseStatus? CloseStatus { get; set; }
    }
}
