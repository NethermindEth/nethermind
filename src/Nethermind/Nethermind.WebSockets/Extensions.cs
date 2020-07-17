//  Copyright (c) 2018 Demerzel Solutions Limited
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

using System;
using System.Buffers;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.WebSockets
{
    public static class Extensions
    {
        public static void UseWebSocketsModules(this IApplicationBuilder app)
        {
            IWebSocketsManager webSocketsManager;
            ILogger logger;
            using (var scope = app.ApplicationServices.CreateScope())
            {
                webSocketsManager = scope.ServiceProvider.GetService<IWebSocketsManager>();
                logger = scope.ServiceProvider.GetService<ILogManager>().GetClassLogger();
            }

            app.Use(async (context, next) =>
            {
                var id = string.Empty;
                var client = string.Empty;
                IWebSocketsModule module = null;
                try
                {
                    string moduleName = String.Empty;
                    if (context.Request.Path.HasValue)
                    {
                        var path = context.Request.Path.Value;
                        moduleName = path.Split("/").LastOrDefault();
                    }

                    module = webSocketsManager.GetModule(moduleName);
                    if (module is null)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    if (!module.TryInit(context.Request))
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    client = context.Request.Query.TryGetValue("client", out var clientValues)
                        ? clientValues.FirstOrDefault()
                        : string.Empty;

                    if (logger.IsInfo) logger.Info($"Initializing WebSockets for client: '{client}'.");
                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var socketsClient = webSocketsManager.CreateClient(module, webSocket, client);
                    id = socketsClient.Id;
                    await webSocket.ReceiveAsync(socketsClient);
                }
                catch (Exception ex)
                {
                    logger.Error("WebSockets error.", ex);
                }
                finally
                {
                    if (!(module is null) && !string.IsNullOrWhiteSpace(id))
                    {
                        module.RemoveClient(id);
                        if (logger.IsInfo) logger.Info($"Closing WebSockets for client: '{client}'.");
                    }
                }
            });
        }

        public const int _maxPooledSize = 1024 * 1024;

        public static async Task ReceiveAsync(this WebSocket webSocket, IWebSocketsClient client)
        {
            int currentMessageLength = 0;
            byte[] buffer = new byte[1024 * 4];
            byte[] combinedData = Array.Empty<byte>();

            WebSocketReceiveResult result = null;
            Task<WebSocketReceiveResult> receiveBeforeTheLoop = webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            await receiveBeforeTheLoop.ContinueWith(t =>
            {
                if (t.IsFaulted)
                { 
                    // TODO: how to log it from here
                }

                if (t.IsCompletedSuccessfully)
                {
                    result = t.Result;
                }
            });

            if (result == null)
            {
                // TODO: how to log it from here
                return;
            }

            while (result.MessageType != WebSocketMessageType.Close)
            {
                int newMessageLength = currentMessageLength + result.Count;
                if (newMessageLength > _maxPooledSize)
                {
                    throw new InvalidOperationException("Message too long");
                }

                byte[] newBytes = ArrayPool<byte>.Shared.Rent(newMessageLength);
                buffer.AsSpan(0, result.Count).CopyTo(newBytes.AsSpan(currentMessageLength, result.Count));
                if (!ReferenceEquals(combinedData, Array.Empty<byte>()))
                {
                    combinedData.AsSpan(0, currentMessageLength).CopyTo(newBytes.AsSpan(0, currentMessageLength));
                    ArrayPool<byte>.Shared.Return(combinedData);
                }

                combinedData = newBytes;
                currentMessageLength = newMessageLength;

                if (result.EndOfMessage)
                {
                    Memory<byte> data = combinedData.AsMemory().Slice(0, currentMessageLength);
                    await client.ReceiveAsync(data);
                    currentMessageLength = 0;
                    ArrayPool<byte>.Shared.Return(combinedData);
                    combinedData = Array.Empty<byte>();
                }

                Task<WebSocketReceiveResult> receiveInTheLoop = webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                await receiveInTheLoop.ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        result = null;
                        // TODO: how to log it from here
                    }

                    if (t.IsCompletedSuccessfully)
                    {
                        result = t.Result;
                    }
                });

                if (result == null)
                {
                    // TODO: how to log it from here
                    return;
                }
            }

            await webSocket.CloseAsync(result.CloseStatus ?? WebSocketCloseStatus.Empty, result.CloseStatusDescription, CancellationToken.None);
        }
    }
}