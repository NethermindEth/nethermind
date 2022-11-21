// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Nethermind.Logging;

namespace Nethermind.Sockets
{
    public static class Extensions
    {
        public static void UseWebSocketsModules(this IApplicationBuilder app)
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
                        module.CreateClient(webSocket, clientName, context);
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
