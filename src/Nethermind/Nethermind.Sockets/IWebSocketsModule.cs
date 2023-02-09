// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace Nethermind.Sockets
{
    public interface IWebSocketsModule
    {
        string Name { get; }
        ISocketsClient CreateClient(WebSocket webSocket, string client, HttpContext context);
        void RemoveClient(string clientId);
        Task SendAsync(SocketsMessage message);
    }
}
