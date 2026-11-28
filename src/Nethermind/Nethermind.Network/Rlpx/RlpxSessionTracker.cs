// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx;

/// <summary>
/// Tracks the sessions of an RLPx host from activation until they disconnect, and raises the host's session events.
/// </summary>
/// <remarks>Shared by the RLPx hosts, so that each one owns only its transport.</remarks>
internal sealed class RlpxSessionTracker : ISessionActivityObserver
{
    private readonly object _host;
    private readonly ISessionMonitor _sessionMonitor;
    private readonly RlpxDialPolicy _dialPolicy;
    private readonly ILogger _logger;
    private readonly Func<CancellationToken> _shutdownToken;
    private readonly ConcurrentDictionary<Guid, Session> _trackedSessions = new();
    private readonly Action<Task, object?> _onConnectionClosed;
    private readonly Action<Task, object?> _markDisconnectedAfterCloseDelay;

    /// <param name="host">The host, as the sender of the session events.</param>
    /// <param name="sessionMonitor">The monitor that pings the active sessions.</param>
    /// <param name="dialPolicy">The policy whose filter a session's activity refreshes.</param>
    /// <param name="logger">The host's logger.</param>
    /// <param name="shutdownToken">Gets the token that is canceled when the host shuts down.</param>
    public RlpxSessionTracker(object host, ISessionMonitor sessionMonitor, RlpxDialPolicy dialPolicy, ILogger logger, Func<CancellationToken> shutdownToken)
    {
        _host = host;
        _sessionMonitor = sessionMonitor;
        _dialPolicy = dialPolicy;
        _logger = logger;
        _shutdownToken = shutdownToken;
        _onConnectionClosed = OnConnectionClosed;
        _markDisconnectedAfterCloseDelay = MarkDisconnectedAfterCloseDelay;
    }

    public event EventHandler<SessionEventArgs>? SessionCreated;

    public event SessionDisconnectedEventHandler? SessionDisconnected;

    /// <summary>Starts tracking a session's activity and disconnect.</summary>
    public void Track(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _trackedSessions[session.SessionId] = session;
        session.SetActivityObserver(this);
    }

    /// <summary>Publishes a session whose connection is open, and marks it disconnected once the connection closes.</summary>
    /// <param name="session">The session.</param>
    /// <param name="connectionClosed">Completes when the session's connection has closed.</param>
    public void Activate(Session session, Task connectionClosed)
    {
        Track(session);
        _sessionMonitor.AddSession(session);

        _ = connectionClosed.ContinueWith(
            _onConnectionClosed,
            session,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        SessionCreated?.Invoke(_host, new SessionEventArgs(session));
    }

    /// <summary>Marks the sessions still tracked at shutdown as disconnected.</summary>
    /// <remarks>Sessions already disconnected during shutdown were removed by the activity observer.</remarks>
    public void DisconnectRemaining()
    {
        foreach (KeyValuePair<Guid, Session> entry in _trackedSessions)
        {
            Session session = entry.Value;
            try
            {
                session.MarkDisconnected(DisconnectReason.AppClosing, DisconnectType.Local, "shutdown");
            }
            catch (InvalidOperationException e)
            {
                if (_logger.IsDebug) _logger.Debug($"Could not disconnect {session:hide} on shutdown: {e.Message}");
            }
        }
    }

    private void OnConnectionClosed(Task _, object? state)
    {
        if (state is Session session)
        {
            session.MarkChannelClosed();
        }

        // The close completion is completed before actual closing or remaining packet is processed.
        // So usually, we do get a disconnect reason from peer, we just receive it after this. So we need to
        // add some delay to account for whatever is holding the network pipeline.
        _ = Task.Delay(TimeSpan.FromSeconds(1), _shutdownToken()).ContinueWith(
            _markDisconnectedAfterCloseDelay,
            state,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void MarkDisconnectedAfterCloseDelay(Task _, object? state)
    {
        ISession session = (ISession)state!;
        if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {session:hide} channel disconnected");
        session.MarkDisconnected(DisconnectReason.ConnectionClosed, DisconnectType.Remote, "channel disconnected");
    }

    void ISessionActivityObserver.OnSessionActivity(Session session) => _dialPolicy.OnSessionActivity(session.Node);

    void ISessionActivityObserver.OnSessionDisconnected(Session session, DisconnectEventArgs args)
    {
        if (!_trackedSessions.TryRemove(session.SessionId, out _))
        {
            return;
        }

        session.SetActivityObserver(null);
        _sessionMonitor.RemoveSession(session);
        try
        {
            SessionDisconnected?.Invoke(_host, session, args);
        }
        finally
        {
            session.Dispose();
        }
    }
}
