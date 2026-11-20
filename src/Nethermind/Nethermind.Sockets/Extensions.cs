// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Nethermind.Logging;

namespace Nethermind.Sockets;

public static class Extensions
{
    /// <summary>
    /// How long a client gets to answer the close sent at shutdown before its connection is aborted.
    /// </summary>
    private static readonly TimeSpan ShutdownCloseTimeout = TimeSpan.FromSeconds(1);

    /// <param name="app">The application to serve WebSockets on.</param>
    /// <param name="stoppingToken">
    /// Signals shutdown. Open sockets are then closed with <see cref="WebSocketCloseStatus.EndpointUnavailable"/>
    /// (Going Away), since the server would otherwise wait for them until its shutdown timeout.
    /// </param>
    public static void UseWebSocketsModules(this IApplicationBuilder app, CancellationToken stoppingToken)
    {
        IWebSocketsManager? webSocketsManager;
        ILogger logger;
        using (IServiceScope scope = app.ApplicationServices.CreateScope())
        {
            webSocketsManager = scope.ServiceProvider.GetService<IWebSocketsManager>();
            logger = scope.ServiceProvider.GetService<ILogManager>()?.GetClassLogger(typeof(Extensions)) ?? default;
        }

        app.Run(async (context) =>
        {
            string id = string.Empty;
            string clientName = string.Empty;
            IWebSocketsModule? module = null;
            try
            {
                string moduleName = string.Empty;
                if (context.Request.Path.HasValue)
                {
                    string path = context.Request.Path.Value;
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

                if (logger.IsDebug) logger.Info($"Initializing WebSockets for client: '{clientName:hide}'.");

                using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                using ISocketsClient socketsClient = await module.CreateClient(webSocket, clientName, context);
                id = socketsClient.Id;
                using CancellationTokenSource abort = new();
                await using (stoppingToken.Register(() => _ = CloseForShutdownAsync(webSocket, abort, logger)))
                {
                    await socketsClient.ReceiveLoopAsync(abort.Token);
                }
            }
            // Open connections are aborted at shutdown, so a failure on an aborted connection is not a node error.
            catch (Exception ex) when (context.RequestAborted.IsCancellationRequested || stoppingToken.IsCancellationRequested)
            {
                if (logger.IsTrace) logger.Trace($"WebSockets connection aborted: {ex.GetType().Name}: {ex.Message}");
            }
            catch (SocketSendLimitException ex)
            {
                if (logger.IsDebug) logger.Debug($"WebSockets connection closed: {ex.Message}");
            }
            catch (WebSocketException ex)
            {
                if (ex.WebSocketErrorCode == WebSocketError.InvalidState)
                {
                    // Websocket closed
                    if (logger.IsDebug) logger.Debug($"WebSockets error {ex.WebSocketErrorCode}: {ex.WebSocketErrorCode} {ex}");
                }
                else
                {
                    if (logger.IsError) logger.Error($"WebSockets error {ex.WebSocketErrorCode}: {ex.WebSocketErrorCode} {ex.Message}", ex);
                }
            }
            catch (Exception ex)
            {
                logger.Error($"WebSockets error {ex.Message}", ex);
            }
            finally
            {
                if (module is not null && !string.IsNullOrWhiteSpace(id))
                {
                    module.RemoveClient(id);
                    if (logger.IsDebug) logger.Info($"Closing WebSockets for client: '{clientName:hide}'.");
                }
            }
        });
    }

    /// <summary>
    /// Sends Going Away so that the client closes the connection, and aborts it if the client does not close it in time.
    /// </summary>
    /// <remarks>Cancelling <paramref name="abort"/> also cancels the requests the client still has in progress.</remarks>
    private static async Task CloseForShutdownAsync(WebSocket webSocket, CancellationTokenSource abort, ILogger logger)
    {
        try
        {
            abort.CancelAfter(ShutdownCloseTimeout);
            await webSocket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "Server shutting down", abort.Token);
        }
        catch (Exception ex)
        {
            // The socket may already be closing, closed or aborted.
            if (logger.IsTrace) logger.Trace($"WebSockets close at shutdown failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
