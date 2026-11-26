// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using DotNetty.Handlers.Timeout;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Stats.Model;

namespace Nethermind.Network.P2P;

/// <summary>
/// Handles an error in the communication with a peer, as every RLPx transport does.
/// </summary>
internal static class RlpxErrorHandler
{
    /// <summary>Logs the error and disconnects the peer, unless the error is internal or the peer is static or trusted.</summary>
    /// <param name="session">The peer's session.</param>
    /// <param name="exception">The error.</param>
    /// <param name="logger">The transport's logger.</param>
    /// <returns>
    /// <see langword="false"/> for a static or trusted peer, which is not disconnected: the transport passes the error on.
    /// </returns>
    public static bool TryHandle(ISession session, Exception exception, ILogger logger)
    {
        if (exception is SocketException or RlpReaderException or ReadTimeoutException)
        {
            if (logger.IsTrace) TraceCommunicationError(session, exception, logger);
        }
        else
        {
            if (logger.IsDebug) logger.Debug($"Error in communication with {GetClientId(session):hide}: {exception:hide}");
        }

        if (exception is IInternalNethermindException)
        {
            // Do nothing as we don't want to drop peer for internal issue.
            return true;
        }

        if (session?.Node?.IsStatic != true && session?.Node?.IsTrusted != true)
        {
            DisconnectReason reason = exception switch
            {
                SocketException { SocketErrorCode: SocketError.ConnectionReset } => DisconnectReason.ConnectionReset,
                RlpLimitException => DisconnectReason.MessageLimitsBreached,
                _ => DisconnectReason.Exception,
            };
            session.InitiateDisconnect(reason, $"Error in communication with {GetClientId(session)} ({exception.GetType().Name}): {exception.Message}");
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TraceCommunicationError(ISession session, Exception exception, ILogger logger) =>
        logger.Trace($"Error in communication with {GetClientId(session):hide} ({exception.GetType().Name}): {exception:hide}");

    private static string GetClientId(ISession? session) =>
        session?.Node?.ToString(Node.Format.Console) ?? $"unknown {session?.RemoteHost}";
}
