/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
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

            var webSocketOptions = new WebSocketOptions
            {
                KeepAliveInterval = TimeSpan.FromSeconds(120),
                ReceiveBufferSize = 4 * 1024
            };
            app.UseWebSockets(webSocketOptions);
            app.Use(async (context, next) =>
            {
                try
                {
                    if (!context.Request.Path.HasValue)
                    {
                        await next();
                        return;
                    }

                    var path = context.Request.Path.Value;
                    if (!path.StartsWith("/ws"))
                    {
                        await next();
                        return;
                    }

                    if (!context.WebSockets.IsWebSocketRequest)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    var moduleName = path.Split("/").LastOrDefault();
                    var module = webSocketsManager.GetModule(moduleName);
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

                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var client = webSocketsManager.CreateClient(module, webSocket);
                    await ReceiveAsync(webSocket, client);
                    module.RemoveClient(client.Id);
                }
                catch (Exception e)
                {
                    logger.Error("WebSocket handling error", e);
                }
            });
        }

        private static async Task ReceiveAsync(WebSocket webSocket, IWebSocketsClient client)
        {
            var buffer = new byte[1024 * 4];
            var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (!result.CloseStatus.HasValue)
            {
                var data = buffer.Slice(0, result.Count);
                await client.ReceiveAsync(data);
                result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }

            await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
        }
    }
}