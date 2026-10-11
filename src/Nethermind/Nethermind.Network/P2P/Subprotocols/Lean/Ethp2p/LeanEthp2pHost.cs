// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Config;
using PublicKey = Nethermind.Core.Crypto.PublicKey;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>Listens for and dials ethp2p QUIC connections of the EIP-8437 profile, at most one per authenticated node key.</summary>
/// <remarks>
/// Peers are bootstrapped from signed ENRs advertising <c>leanq</c> (<see cref="INetworkConfig.LeanEthp2pStaticPeers"/>);
/// the local ENR advertises the listening port. Connections share the node's budgets with its RLPx session, and the shared
/// transport refuses every Status until EIP-8288 activates. Authenticated broadcast runs only with a registered
/// <see cref="ILeanBroadcastProfile"/>; without one the node uses requested retrieval only.
/// </remarks>
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("windows")]
public sealed class LeanEthp2pHost(LeanObjectTransport transport, INetworkConfig config,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey, ILogManager logManager, ILeanBroadcastProfile? broadcastProfile = null)
    : IAsyncDisposable
{
    private static readonly TimeSpan DialInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxDialBackoff = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(15);
    private const int ListenBacklog = 16;

    private readonly LeanObjectTransport _transport = transport;
    private readonly INetworkConfig _config = config;
    private readonly IProtectedPrivateKey _nodeKey = nodeKey;
    private readonly ILogManager _logManager = logManager;
    private readonly ILogger _logger = logManager.GetClassLogger<LeanEthp2pHost>();
    private readonly LeanBroadcastEngine? _engine = broadcastProfile is null ? null : new LeanBroadcastEngine(transport, broadcastProfile, logManager);
    private readonly Lock _lock = new();
    private readonly Dictionary<PublicKey, LeanEthp2pConnection> _connections = [];
    private readonly HashSet<PublicKey> _dialing = [];
    private readonly Dictionary<PublicKey, (DateTimeOffset Next, TimeSpan Backoff)> _backoff = [];
    private readonly List<Task> _running = [];
    private readonly CancellationTokenSource _stop = new();
    private X509Certificate2? _certificate;
    private QuicListener? _listener;
    private Task? _loops;
    private bool _disposed;

    /// <summary>Whether this platform provides QUIC (libmsquic on Linux and macOS).</summary>
    public static bool IsSupported => QuicListener.IsSupported && QuicConnection.IsSupported;

    /// <summary>The bound UDP endpoint, or null before start or when QUIC is unavailable.</summary>
    public IPEndPoint? LocalEndPoint { get; private set; }

    internal int ConnectionCount { get { lock (_lock) return _connections.Count; } }

    internal LeanBroadcastEngine? Broadcast => _engine;

    /// <summary>Binds the listener and starts dialing the configured static peers.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsSupported)
        {
            if (_logger.IsWarn) _logger.Warn("lean/1 ethp2p binding is enabled but QUIC is unavailable (install libmsquic); lean/1 continues over RLPx only");
            return;
        }
        List<LeanEthp2pRecord> peers = ParseStaticPeers();
        _certificate = LeanEthp2pIdentity.CreateCertificate(_nodeKey.Unprotect());
        QuicListener listener = await QuicListener.ListenAsync(new QuicListenerOptions
        {
            ListenEndPoint = new IPEndPoint(ListenAddress(), _config.LeanEthp2pPort),
            ApplicationProtocols = [LeanEthp2pProtocol.ApplicationProtocol],
            ListenBacklog = ListenBacklog,
            ConnectionOptionsCallback = (_, _, _) => ValueTask.FromResult(ServerOptions())
        }, cancellationToken).ConfigureAwait(false);
        _listener = listener;
        LocalEndPoint = listener.LocalEndPoint;
        if (_logger.IsInfo) _logger.Info($"lean/1 ethp2p binding listening on udp/{LocalEndPoint.Port} with {peers.Count} static peers, " +
            $"{(_engine is null ? "retrieval only" : "retrieval and broadcast")}");
        _loops = Task.WhenAll(AcceptAsync(listener), DialStaticAsync(peers));
    }

    private IPAddress ListenAddress() =>
        _config.LocalIp is { Length: > 0 } local ? IPAddress.Parse(local)
        : Socket.OSSupportsIPv6 && !OperatingSystem.IsMacOS() ? IPAddress.IPv6Any : IPAddress.Any;

    private List<LeanEthp2pRecord> ParseStaticPeers()
    {
        List<LeanEthp2pRecord> peers = [];
        if (_config.LeanEthp2pStaticPeers is not { } configured) return peers;
        foreach (string enr in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (LeanEthp2pRecord.TryParse(enr, out LeanEthp2pRecord? record, out string? error)) peers.Add(record);
            else if (_logger.IsWarn) _logger.Warn($"Ignoring lean/1 ethp2p static peer: {error}");
        }
        return peers;
    }

    private QuicServerConnectionOptions ServerOptions() => Configure(new QuicServerConnectionOptions
    {
        DefaultStreamErrorCode = LeanEthp2pProtocol.RequestCancelledError,
        DefaultCloseErrorCode = LeanEthp2pProtocol.NoError,
        ServerAuthenticationOptions = new SslServerAuthenticationOptions
        {
            ApplicationProtocols = [LeanEthp2pProtocol.ApplicationProtocol],
            ServerCertificate = _certificate,
            ClientCertificateRequired = true,
            EnabledSslProtocols = SslProtocols.Tls13,
            RemoteCertificateValidationCallback = static (_, certificate, _, _) => Authenticate(certificate) is not null
        }
    });

    private static T Configure<T>(T options) where T : QuicConnectionOptions
    {
        options.MaxInboundUnidirectionalStreams = LeanEthp2pProtocol.MaxInboundStreams;
        options.MaxInboundBidirectionalStreams = 0;
        options.HandshakeTimeout = HandshakeTimeout;
        options.IdleTimeout = IdleTimeout;
        options.KeepAliveInterval = KeepAlive;
        options.InitialReceiveWindowSizes = new QuicReceiveWindowSizes
        {
            Connection = LeanEthp2pProtocol.ConnectionReceiveWindow,
            UnidirectionalStream = LeanEthp2pProtocol.StreamReceiveWindow
        };
        return options;
    }

    /// <summary>The authenticated secp256k1 node key of a presented certificate.</summary>
    internal static PublicKey? Authenticate(X509Certificate? certificate) =>
        certificate is not null && LeanEthp2pIdentity.TryAuthenticate(certificate.GetRawCertData(), DateTimeOffset.UtcNow, out PublicKey? key, out _)
            ? key : null;

    private async Task AcceptAsync(QuicListener listener)
    {
        CancellationToken token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            QuicConnection quic;
            try
            {
                quic = await listener.AcceptConnectionAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is QuicException or AuthenticationException or OperationCanceledException)
            {
                // A failed handshake, wrong ALPN or rejected certificate ends only that attempt.
                if (_logger.IsDebug) _logger.Debug($"lean/1 ethp2p inbound handshake failed: {exception.Message}");
                continue;
            }
            if (Authenticate(quic.RemoteCertificate) is not { } key)
            {
                await quic.DisposeAsync().ConfigureAwait(false);
                continue;
            }
            await Register(new LeanEthp2pConnection(quic, key, outbound: false, _transport, _engine, _logManager)).ConfigureAwait(false);
        }
    }

    /// <summary>Dials a peer and verifies that its TLS identity is the node key of its signed record.</summary>
    internal async Task<LeanEthp2pConnection?> DialAsync(LeanEthp2pRecord record, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_certificate is null, this);
        PublicKey expected = record.NodeKey;
        QuicClientConnectionOptions options = Configure(new QuicClientConnectionOptions
        {
            RemoteEndPoint = record.EndPoint,
            DefaultStreamErrorCode = LeanEthp2pProtocol.RequestCancelledError,
            DefaultCloseErrorCode = LeanEthp2pProtocol.NoError,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = [LeanEthp2pProtocol.ApplicationProtocol],
                TargetHost = LeanEthp2pProtocol.ServerName,
                ClientCertificates = [_certificate],
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = (_, certificate, _, _) => Authenticate(certificate) is { } key && key.Equals(expected)
            }
        });
        QuicConnection quic;
        try
        {
            quic = await QuicConnection.ConnectAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is QuicException or AuthenticationException or OperationCanceledException or SocketException)
        {
            if (_logger.IsDebug) _logger.Debug($"lean/1 ethp2p dial to {record.EndPoint} failed: {exception.Message}");
            return null;
        }
        LeanEthp2pConnection connection = new(quic, expected, outbound: true, _transport, _engine, _logManager);
        return await Register(connection).ConfigureAwait(false) ? connection : null;
    }

    private async Task<bool> Register(LeanEthp2pConnection connection)
    {
        PublicKey localKey = _nodeKey.PublicKey;
        LeanEthp2pConnection? replaced = null;
        string? refusal = null;
        lock (_lock)
        {
            if (_disposed) refusal = "host stopped";
            else if (connection.RemoteKey.Equals(localKey)) refusal = "connection to self";
            else if (_connections.TryGetValue(connection.RemoteKey, out LeanEthp2pConnection? existing))
            {
                // Both ends keep the same one of two simultaneous connections, so the node never has two.
                if (Prefer(existing, connection, localKey)) refusal = "duplicate connection";
                else replaced = existing;
            }
            else if (_connections.Count >= _config.MaxActivePeers) refusal = "connection limit";

            if (refusal is null)
            {
                _connections[connection.RemoteKey] = connection;
                _running.Add(RunAsync(connection));
                _running.RemoveAll(static task => task.IsCompleted);
            }
        }
        replaced?.Close(LeanEthp2pProtocol.NoError, "replaced by a preferred connection");
        if (refusal is null) return true;
        if (_logger.IsDebug) _logger.Debug($"lean/1 ethp2p {connection} refused: {refusal}");
        await connection.DisposeAsync().ConfigureAwait(false);
        return false;
    }

    /// <summary>Whether to keep <paramref name="existing"/> over <paramref name="candidate"/> from the same node.</summary>
    /// <remarks>Of opposite-direction connections, both ends keep the one dialed by the numerically lower node key.</remarks>
    private static bool Prefer(LeanEthp2pConnection existing, LeanEthp2pConnection candidate, PublicKey localKey)
    {
        if (existing.Outbound == candidate.Outbound) return false;
        PublicKey existingDialer = existing.Outbound ? localKey : existing.RemoteKey;
        PublicKey candidateDialer = candidate.Outbound ? localKey : candidate.RemoteKey;
        return existingDialer.Bytes.AsSpan().SequenceCompareTo(candidateDialer.Bytes) < 0;
    }

    private async Task RunAsync(LeanEthp2pConnection connection)
    {
        await Task.Yield();
        try
        {
            await connection.RunAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error($"lean/1 ethp2p {connection} failed", exception);
        }
        finally
        {
            lock (_lock)
            {
                if (_connections.TryGetValue(connection.RemoteKey, out LeanEthp2pConnection? current) && current == connection) _connections.Remove(connection.RemoteKey);
            }
            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task DialStaticAsync(List<LeanEthp2pRecord> peers)
    {
        if (peers.Count == 0) return;
        using PeriodicTimer timer = new(DialInterval);
        CancellationToken token = _stop.Token;
        try
        {
            do
            {
                if (!_transport.IsEnabled) continue;
                foreach (LeanEthp2pRecord peer in peers) TryDial(peer, token);
            } while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void TryDial(LeanEthp2pRecord peer, CancellationToken token)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (_connections.ContainsKey(peer.NodeKey) || !_dialing.Add(peer.NodeKey)) return;
            if (_backoff.TryGetValue(peer.NodeKey, out (DateTimeOffset Next, TimeSpan Backoff) backoff) && backoff.Next > now)
            {
                _dialing.Remove(peer.NodeKey);
                return;
            }
        }
        _ = DialWithBackoffAsync(peer, token);
    }

    private async Task DialWithBackoffAsync(LeanEthp2pRecord peer, CancellationToken token)
    {
        // Failure to negotiate the binding only delays a bounded retry; RLPx stays available.
        bool connected = await DialAsync(peer, token).ConfigureAwait(false) is not null;
        lock (_lock)
        {
            _dialing.Remove(peer.NodeKey);
            if (connected) _backoff.Remove(peer.NodeKey);
            else
            {
                TimeSpan previous = _backoff.TryGetValue(peer.NodeKey, out (DateTimeOffset, TimeSpan Backoff) entry) ? entry.Backoff : DialInterval / 2;
                TimeSpan next = previous * 2 > MaxDialBackoff ? MaxDialBackoff : previous * 2;
                _backoff[peer.NodeKey] = (DateTimeOffset.UtcNow + next, next);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task[] running;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            running = [.. _running];
            foreach (LeanEthp2pConnection connection in _connections.Values) connection.Close(LeanEthp2pProtocol.NoError, "shutting down");
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listener is not null) await _listener.DisposeAsync().ConfigureAwait(false);
        if (_loops is not null) await _loops.ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
        _engine?.Dispose();
        _certificate?.Dispose();
        _stop.Dispose();
    }
}
