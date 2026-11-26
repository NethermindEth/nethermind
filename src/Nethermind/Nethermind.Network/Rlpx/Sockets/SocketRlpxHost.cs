// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core.ServiceStopper;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.Rlpx.Handshake;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Rlpx.Sockets;

/// <summary>
/// An RLPx host on sockets and System.IO.Pipelines, without DotNetty.
/// </summary>
/// <remarks>
/// Selected with <see cref="INetworkConfig.RlpxTransport"/>. It shares its dial, filter and session policy with
/// <see cref="RlpxHost"/>, and owns only the transport: the listener, the dials and the connections.
/// </remarks>
public sealed class SocketRlpxHost : IRlpxHost, IStoppableService
{
    private const int ListenBacklog = 100;
    private static readonly TimeSpan AcceptRetryDelay = TimeSpan.FromMilliseconds(100);

    // DotNetty starts no connect timer for a zero timeout, which leaves RlpxHost's 2-second watchdog.
    private static readonly TimeSpan ZeroConnectTimeoutDeadline = TimeSpan.FromSeconds(2);

    private readonly IMessageSerializationService _serializationService;
    private readonly IHandshakeService _handshakeService;
    private readonly IDisconnectsAnalyzer _disconnectsAnalyzer;
    private readonly ILogManager _logManager;
    private readonly ILogger _logger;
    private readonly NetworkListenerState _listenerState;
    private readonly RlpxDialPolicy _dialPolicy;
    private readonly RlpxSessionTracker _sessions;
    private readonly Func<Node, CancellationToken, Task<bool>> _tryConnect;
    private readonly TimeSpan _sendLatency;
    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _shutdownCloseTimeout;
    private readonly int _processingThreadCount;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _listenerClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Guards the shutdown flag, the connections and the pending dials, so that shutdown sees every one of them.
    private readonly Lock _lock = new();
    private readonly HashSet<SocketRlpxConnection> _connections = [];
    private readonly HashSet<Task> _pendingDials = [];
    private bool _shuttingDown;

    private RlpxProcessingThreads? _processingThreads;
    private Socket? _listener;
    private Task _acceptLoop = Task.CompletedTask;
    private bool _isInitialized;

    public SocketRlpxHost(
        IMessageSerializationService serializationService,
        IHandshakeService handshakeService,
        ISessionMonitor sessionMonitor,
        IDisconnectsAnalyzer disconnectsAnalyzer,
        INetworkConfig networkConfig,
        IIPResolver ipResolver,
        IPrivilegedIpProvider privilegedIpProvider,
        ILogManager logManager,
        NetworkListenerState listenerState)
    {
        ArgumentNullException.ThrowIfNull(serializationService);
        ArgumentNullException.ThrowIfNull(handshakeService);
        ArgumentNullException.ThrowIfNull(sessionMonitor);
        ArgumentNullException.ThrowIfNull(disconnectsAnalyzer);
        ArgumentNullException.ThrowIfNull(networkConfig);
        ArgumentNullException.ThrowIfNull(logManager);

        _serializationService = serializationService;
        _handshakeService = handshakeService;
        _disconnectsAnalyzer = disconnectsAnalyzer;
        _logManager = logManager;
        _logger = logManager.GetClassLogger<SocketRlpxHost>();
        _listenerState = listenerState;
        SessionMonitor = sessionMonitor;
        LocalPort = networkConfig.P2PPort;
        // The peer filter needs the resolved external address before it can accept connections.
        IIPResolver.NethermindIp ips = ipResolver.Resolve().GetAwaiter().GetResult();
        _sendLatency = TimeSpan.FromMilliseconds(networkConfig.SimulateSendLatencyMs);
        _connectTimeout = networkConfig.ConnectTimeoutMs > 0 ? TimeSpan.FromMilliseconds(networkConfig.ConnectTimeoutMs) : ZeroConnectTimeoutDeadline;
        _shutdownCloseTimeout = TimeSpan.FromMilliseconds(networkConfig.RlpxHostShutdownCloseTimeoutMs);
        _processingThreadCount = networkConfig.ProcessingThreadCount;
        _tryConnect = TryConnect;
        NodeFilter nodeFilter = NodeFilter.Create(networkConfig.MaxActivePeers, networkConfig.FilterPeersByRecentIp, networkConfig.FilterPeersBySameSubnet, ips.ExternalIp);
        _dialPolicy = new RlpxDialPolicy(nodeFilter, privilegedIpProvider, _logger);
        _sessions = new RlpxSessionTracker(this, sessionMonitor, _dialPolicy, _logger, () => _shutdown.Token);
    }

    public int LocalPort { get; }

    public ISessionMonitor SessionMonitor { get; }

    public event EventHandler<SessionEventArgs> SessionCreated
    {
        add => _sessions.SessionCreated += value;
        remove => _sessions.SessionCreated -= value;
    }

    public event SessionDisconnectedEventHandler SessionDisconnected
    {
        add => _sessions.SessionDisconnected += value;
        remove => _sessions.SessionDisconnected -= value;
    }

    /// <summary>Gets the endpoint the listener is bound to, once initialized.</summary>
    internal IPEndPoint? ListenerEndPoint => _listener?.LocalEndPoint as IPEndPoint;

    internal int ConnectionCount
    {
        get
        {
            lock (_lock)
            {
                return _connections.Count;
            }
        }
    }

    public bool ShouldContact(IPAddress ip, bool exactOnly = false) => _dialPolicy.ShouldContact(ip, exactOnly);

    public async Task Init()
    {
        if (_isInitialized)
        {
            ThrowAlreadyInitialized();
        }

        _isInitialized = true;
        _processingThreads = new RlpxProcessingThreads(_processingThreadCount, _logger);
        try
        {
            _listener = await RlpxListener.BindAsync(_listenerState, LocalPort, _logger, nameof(SocketRlpxHost), Bind);
        }
        catch (Exception ex)
        {
            _logger.Error($"{nameof(Init)} failed.", ex);
            await _processingThreads.StopAsync();
            throw;
        }

        if (_listener.LocalEndPoint is IPEndPoint endpoint)
        {
            _ = _listenerState.TrackRlpxAddress(endpoint.Address, _listenerClosed.Task);
        }

        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private Task<Socket> Bind(IPAddress address)
    {
        Socket socket = RlpxListener.CreateSocket(address);
        try
        {
            NetworkHelper.HandlePortTakenError(() =>
            {
                socket.Bind(new IPEndPoint(address, LocalPort));
                socket.Listen(ListenBacklog);
                return socket;
            }, LocalPort);
            return Task.FromResult(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowAlreadyInitialized()
        => throw new InvalidOperationException($"{nameof(SocketRlpxHost)} already initialized.");

    private async Task AcceptLoopAsync()
    {
        Socket listener = _listener!;
        try
        {
            while (true)
            {
                Socket socket;
                try
                {
                    socket = await listener.AcceptAsync(_shutdown.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException || _shutdown.IsCancellationRequested)
                {
                    return;
                }
                catch (SocketException e)
                {
                    if (_logger.IsDebug) _logger.Debug($"Failed to accept an RLPx connection: {e.Message}");
                    // A peer resetting before it is accepted is routine; other errors may persist, so they don't spin the loop.
                    if (e.SocketErrorCode != SocketError.ConnectionReset && !await PauseAcceptingAsync())
                    {
                        return;
                    }

                    continue;
                }

                Accept(socket);
            }
        }
        finally
        {
            _listenerClosed.TrySetResult();
        }
    }

    /// <returns><see langword="false"/> if the host shut down meanwhile.</returns>
    private async Task<bool> PauseAcceptingAsync()
    {
        try
        {
            await Task.Delay(AcceptRetryDelay, _shutdown.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Accept(Socket socket)
    {
        IPEndPoint remote;
        try
        {
            remote = (IPEndPoint)socket.RemoteEndPoint!;
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            socket.Dispose();
            return;
        }

        SocketRlpxConnection connection = CreateConnection(socket, HandshakeRole.Recipient);
        IPAddress remoteIp = remote.Address.NormalizeMappedIPv4();
        Session session = new(LocalPort, connection, _disconnectsAnalyzer, _logManager)
        {
            RemoteHost = remoteIp.ToString(),
            RemotePort = remote.Port
        };

        if (!TryTrack(connection))
        {
            return;
        }

        Metrics.IncomingConnections++;
        if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Initializing {session:hide} channel");

        if (!_dialPolicy.AcceptsInbound(remoteIp))
        {
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Rejecting inbound connection from filtered IP {remoteIp:hide}");
            Untrack(connection);
            _ = connection.DisconnectAsync();
            return;
        }

        StartSession(socket, connection, session);
    }

    public Task<bool> ConnectAsync(Node node, CancellationToken cancellationToken = default)
        => _dialPolicy.ConnectAsync(node, _tryConnect, cancellationToken);

    private async Task<bool> TryConnect(Node node, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource dial = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            if (_shuttingDown || _processingThreads is null)
            {
                return false;
            }

            _pendingDials.Add(dial.Task);
        }

        try
        {
            Socket socket = new(node.Address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            SocketRlpxConnection connection = CreateConnection(socket, HandshakeRole.Initiator);
            // Constructed before connecting, as RlpxHost constructs it at channel initialization; this starts its pong window.
            Session session = new(LocalPort, node, connection, _disconnectsAnalyzer, _logManager);

            using CancellationTokenSource connectCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            connectCancellation.CancelAfter(_connectTimeout);
            try
            {
                await socket.ConnectAsync(node.Address, connectCancellation.Token);
            }
            catch (Exception e)
            {
                _ = connection.DisconnectAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (e is OperationCanceledException)
                {
                    if (_logger.IsTrace) TraceConnectionTimedOut(node);
                }
                else
                {
                    if (_logger.IsTrace) TraceConnectionFailure(node, e);
                }

                return false;
            }

            // A connection that completes after its deadline was canceled above, so activation has no race to lose.
            if (!TryTrack(connection))
            {
                return false;
            }

            Metrics.OutgoingConnections++;
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| Initializing {session:hide} channel");
            StartSession(socket, connection, session);
            if (_logger.IsTrace) _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} OUT connected");
            return true;
        }
        finally
        {
            lock (_lock)
            {
                _pendingDials.Remove(dial.Task);
            }

            dial.TrySetResult();
        }
    }

    private SocketRlpxConnection CreateConnection(Socket socket, HandshakeRole role)
        => new(
            new TcpRlpxSocket(socket),
            role,
            _handshakeService,
            _serializationService,
            _processingThreads!.Next(),
            _logManager,
            _sendLatency,
            ZeroFrameDecoder.DefaultMaxInboundFrameSize);

    /// <summary>Tracks a connection until its loops finish, or closes it if the host is shutting down.</summary>
    private bool TryTrack(SocketRlpxConnection connection)
    {
        lock (_lock)
        {
            if (!_shuttingDown)
            {
                _connections.Add(connection);
                return true;
            }
        }

        _ = connection.DisconnectAsync();
        return false;
    }

    private void Untrack(SocketRlpxConnection connection)
    {
        lock (_lock)
        {
            _connections.Remove(connection);
        }
    }

    private void StartSession(Socket socket, SocketRlpxConnection connection, Session session)
    {
        SetSocketOption(socket, SocketOptionLevel.Tcp, SocketOptionName.NoDelay);
        SetSocketOption(socket, SocketOptionLevel.Socket, SocketOptionName.KeepAlive);
        _sessions.Activate(session, connection.Closed);
        connection.Start(session);
        _ = connection.Completion.ContinueWith(
            static (_, state) =>
            {
                (SocketRlpxHost host, SocketRlpxConnection connection) = ((SocketRlpxHost, SocketRlpxConnection))state!;
                host.Untrack(connection);
            },
            (this, connection),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void SetSocketOption(Socket socket, SocketOptionLevel level, SocketOptionName name)
    {
        try
        {
            socket.SetSocketOption(level, name, true);
        }
        catch (Exception ex)
        {
            if (_logger.IsWarn) _logger.Warn($"Failed to set socket option {name}: {ex.Message:hide}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceConnectionTimedOut(Node node) =>
        _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} OUT connection timed out");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceConnectionFailure(Node node, Exception exception) =>
        _logger.Trace($"|NetworkTrace| {node.ToString("s"):hide} error when OUT connecting {exception:hide}");

    Task IStoppableService.StopAsync() => Shutdown();

    string IStoppableService.Description => "rlpx host";

    public async Task Shutdown()
    {
        Task[] dials;
        lock (_lock)
        {
            if (_shuttingDown)
            {
                return;
            }

            _shuttingDown = true;
            dials = [.. _pendingDials];
        }

        // Ends the accept loop and the pending dials, and the close delays of the sessions.
        _shutdown.Cancel();
        _listener?.Dispose();
        await _acceptLoop;
        await Task.WhenAll(dials);
        if (_logger.IsDebug) _logger.Debug("Closed the RLPx listener");

        SocketRlpxConnection[] connections;
        lock (_lock)
        {
            connections = [.. _connections];
        }

        try
        {
            await Task.WhenAll(connections.Select(static c => c.DisconnectAsync())).WaitAsync(_shutdownCloseTimeout);
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"Closing peer channels on shutdown failed: {e.Message}");
        }

        // The connections' loops queue their last items before the processing threads finish theirs.
        Task stopping = StopConnectionsAndThreadsAsync(connections);
        TimeSpan warningDelay = _shutdownCloseTimeout * 3 + TimeSpan.FromSeconds(1);
        try
        {
            await stopping.WaitAsync(warningDelay);
        }
        catch (TimeoutException)
        {
            if (_logger.IsWarn) _logger.Warn($"RLPx connections have not stopped after {warningDelay.TotalSeconds} seconds. Waiting for them to stop.");
            await stopping;
        }

        _sessions.DisconnectRemaining();

        if (_logger.IsInfo) _logger.Info("Local peer shutdown complete.. please wait for all components to close");
    }

    private async Task StopConnectionsAndThreadsAsync(SocketRlpxConnection[] connections)
    {
        try
        {
            await Task.WhenAll(connections.Select(static c => c.Completion));
        }
        catch (Exception e)
        {
            if (_logger.IsDebug) _logger.Debug($"An RLPx connection failed while stopping: {e.Message}");
        }

        if (_processingThreads is not null)
        {
            await _processingThreads.StopAsync();
        }
    }
}
