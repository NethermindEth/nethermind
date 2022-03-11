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

using System;
using System.Linq;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Authentication;
using Nethermind.Logging;

namespace Nethermind.Sockets
{
    public static class Extensions
    {
        public static void UseWebSocketsModules(this IApplicationBuilder app, IRpcAuthentication authentication)
        {
            IWebSocketsManager? webSocketsManager;
            ILogger? logger;
            using (var scope = app.ApplicationServices.CreateScope())
            {
                webSocketsManager = scope.ServiceProvider.GetService<IWebSocketsManager>();
                logger = scope.ServiceProvider.GetService<ILogManager>()?.GetClassLogger();
            }

            app.Use(async (context, next) =>
            {
                string id = string.Empty;
                string clientName = string.Empty;
                IWebSocketsModule? module = null;
                try
                {
                    string moduleName = string.Empty;
                    if (context.Request.Path.HasValue)
                    {
                        var path = context.Request.Path.Value;
                        moduleName = path.Split("/", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
                    }

                    module = webSocketsManager?.GetModule(moduleName);
                    if (module is null)
                    {
                        context.Response.StatusCode = 400;
                        return;
                    }

                    clientName = context.Request.Query.TryGetValue("client", out StringValues clientValues)
                        ? clientValues.FirstOrDefault() ?? string.Empty
                        : string.Empty;

                    if (logger?.IsDebug == true) logger.Info($"Initializing WebSockets for client: '{clientName}'.");

                    var webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    var socketsClient =
                        module.CreateClient(webSocket, clientName, context.Connection.LocalPort,
                            authentication.Authenticate(context.Request.Headers["Authorization"]));
                    id = socketsClient.Id;
                    await socketsClient.ReceiveAsync();
                }
                catch (WebSocketException ex)
                {
                    logger?.Error($"WebSockets error {ex.WebSocketErrorCode}: {ex.WebSocketErrorCode} {ex.Message}", ex);
                }
                catch (Exception ex)
                {
                    logger?.Error($"WebSockets error {ex.Message}", ex);
                }
                finally
                {
                    if (!(module is null) && !string.IsNullOrWhiteSpace(id))
                    {
                        module.RemoveClient(id);
                        if (logger?.IsDebug == true) logger.Info($"Closing WebSockets for client: '{clientName}'.");
                    }
                }
            });
        }
    }
}
