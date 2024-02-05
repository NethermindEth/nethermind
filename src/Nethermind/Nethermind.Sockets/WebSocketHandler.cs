// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;

namespace Nethermind.Sockets
{
    //public class WebSocketHandler : ISocketHandler
    //{
    //    private readonly WebSocket _webSocket;
    //    private readonly ILogger _logger;

    //    public WebSocketHandler(WebSocket webSocket, ILogManager logManager)
    //    {
    //        _webSocket = webSocket;
    //        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
    //    }

    //    public Task SendRawAsync(ArraySegment<byte> data, bool endOfMessage = true) =>
    //        _webSocket.State != WebSocketState.Open
    //            ? Task.CompletedTask
    //            : _webSocket.SendAsync(data, WebSocketMessageType.Text, endOfMessage, CancellationToken.None);

        

    //    public Stream SendUsingStream()
    //    {
    //        return new WebSocketStream(_webSocket, WebSocketMessageType.Text);
    //    }

    //    public void Dispose()
    //    {
    //        _webSocket.Dispose();
    //    }
    //}
}
