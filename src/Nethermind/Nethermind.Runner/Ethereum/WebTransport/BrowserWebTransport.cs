// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autofac.Features.AttributeFilters;
using Nethermind.Api.Steps;
using Nethermind.Core.ServiceStopper;
using Nethermind.Crypto;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Config;
using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Stats.Model;
using Nethermind.TxPool;

namespace Nethermind.Runner.Ethereum.WebTransport;

[RunnerStepDependencies(typeof(InitializeNetwork))]
internal sealed class BrowserWebTransport(
    INetworkConfig config,
    NetworkListenerState listenerState,
    INodeRecordProvider records,
    IPeerPool peerPool,
    [KeyFilter(IProtectedPrivateKey.NodeKey)] IProtectedPrivateKey nodeKey,
    IEthereumEcdsa ecdsa,
    ITxPool txPool,
    ILogManager logManager) : IStep, IStoppableService, IAsyncDisposable
{
    private const int WorkerCount = 16;
    private static readonly TimeSpan _certificateRotation = TimeSpan.FromDays(7);
    private readonly BrowserProtocol _protocol = new(ecdsa, nodeKey, txPool);
    private readonly ILogger _logger = logManager.GetClassLogger<BrowserWebTransport>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _peerCacheLock = new();
    private Node[] _cachedPeers = [];
    private long _peerCacheTimestamp;
    private Task? _runTask;

    public Task Execute(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int port = config.WebTransportPort;
        if (port <= 0 || port > ushort.MaxValue)
            throw new InvalidOperationException("Network.WebTransportPort must be a UDP port between 1 and 65535, or 0 to disable it.");
        if (port == config.DiscoveryPort && listenerState.DiscoveryAddress is not null)
            throw new InvalidOperationException("Network.WebTransportPort and Network.DiscoveryPort must use different UDP ports.");

        IPAddress address = listenerState.RlpxAddress ?? listenerState.DiscoveryAddress ?? listenerState.PreferredAddress;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        IPEndPoint endpoint = new(address, port);
        BrowserCertificates certificates = new();
        MsQuicListener listener;
        try
        {
            listener = MsQuicListener.Listen(endpoint, certificates.Current);
        }
        catch
        {
            certificates.Dispose();
            throw;
        }

        _runTask = RunAsync(listener, endpoint, certificates, _shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _shutdown.CancelAsync();
        if (_runTask is not null)
            await _runTask;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync();
        }
        finally
        {
            _protocol.Dispose();
            _shutdown.Dispose();
        }
    }

    private async Task RunAsync(MsQuicListener listener, IPEndPoint endpoint, BrowserCertificates certificates, CancellationToken cancellationToken)
    {
        using BrowserCertificates ownedCertificates = certificates;
        await using MsQuicListener ownedListener = listener;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        PublishBinding(endpoint, certificates);
        if (_logger.IsInfo) _logger.Info($"Browser WebTransport listening on UDP {endpoint:hide}.");

        Task[] workers = new Task[WorkerCount + 1];
        for (int i = 0; i < WorkerCount; i++)
            workers[i] = ServeAsync(listener, lifetime.Token);
        workers[^1] = RotateCertificatesAsync(listener, endpoint, certificates, lifetime.Token);

        try
        {
            Task completed = await Task.WhenAny(workers);
            await completed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_logger.IsError) _logger.Error("Browser WebTransport stopped.", exception);
        }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                await Task.WhenAll(workers);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (_logger.IsDebug) _logger.Debug($"Browser WebTransport worker stopped: {exception.Message}");
            }
            finally
            {
                listenerState.SetWebTransport(null);
            }
        }
    }

    private async Task RotateCertificatesAsync(MsQuicListener listener, IPEndPoint endpoint, BrowserCertificates certificates, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(_certificateRotation, TimeProvider.System);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            certificates.Rotate(listener);
            PublishBinding(endpoint, certificates);
        }
    }

    private void PublishBinding(IPEndPoint endpoint, BrowserCertificates certificates) =>
        listenerState.SetWebTransport(new(endpoint.Address, endpoint.Port, certificates.GetHashes()));

    private async Task ServeAsync(MsQuicListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using MsQuicConnection connection = await listener.AcceptConnectionAsync(cancellationToken);
            try
            {
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                await Http3WebTransport.ServeAsync(connection, BrowserFrames.ParseOffer, ExchangeAsync, deadline.Token);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or OperationCanceledException or RlpReaderException or ChannelClosedException)
            {
                if (_logger.IsTrace) _logger.Trace($"Browser WebTransport request ended: {exception.Message}");
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Browser WebTransport request failed.", exception);
            }
        }
    }

    private async Task ExchangeAsync(Stream stream, byte mode, byte[] nonce, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(_protocol.CreateHandshake(mode, nonce), cancellationToken);
        (IMemoryOwner<byte> owner, int length) = await BrowserFrames.ReadRequestAsync(stream, mode, cancellationToken);
        using (owner)
        {
            byte[] result = mode == BrowserProtocol.TxMode
                ? _protocol.Submit(owner.Memory.Span[..length], cancellationToken)
                : await GetPeersAsync(cancellationToken);

            await BrowserFrames.WriteResponseAsync(stream, result, cancellationToken);
        }
    }

    private async ValueTask<byte[]> GetPeersAsync(CancellationToken cancellationToken)
    {
        bool allowLocalRecords = listenerState.WebTransport is { Address: { } address } && IPAddress.IsLoopback(address);
        NodeRecord self = await records.GetCurrentAsync(cancellationToken);
        List<NodeRecord> available = IsDialable(self, allowLocalRecords) ? [self] : [];

        foreach (Node peer in GetQuicPeers(allowLocalRecords))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (available.Count == BrowserProtocol.MaxPeerRecords)
                break;
            if (GetDialableRecord(peer, allowLocalRecords) is { } record)
                available.Add(record);
        }

        return BrowserProtocol.EncodePeerRecords(available);
    }

    private Node[] GetQuicPeers(bool allowLocalRecords)
    {
        lock (_peerCacheLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_peerCacheTimestamp != 0 && Stopwatch.GetElapsedTime(_peerCacheTimestamp, now) < TimeSpan.FromSeconds(5))
                return _cachedPeers;

            List<Node> peers = [];
            foreach ((_, Peer peer) in peerPool.Peers)
            {
                if (GetDialableRecord(peer.Node, allowLocalRecords) is not null)
                {
                    peers.Add(peer.Node);
                    if (peers.Count == BrowserProtocol.MaxPeerRecords) break;
                }
            }

            _peerCacheTimestamp = now;
            return _cachedPeers = [.. peers];
        }
    }

    private static NodeRecord? GetDialableRecord(Node peer, bool allowLocalRecords) => peer.Enr is { Signature: not null } record &&
        record.EnrSequence >= peer.HighestObservedEnrSequence && peer.IsVerifiedEnr(record) && IsDialable(record, allowLocalRecords) ? record : null;

    private static bool IsDialable(NodeRecord record, bool allowLocalRecords)
    {
        bool hasV4 = record.TryGetQuicEndpoint(AddressFamily.InterNetwork, out IPEndPoint? v4);
        bool hasV6 = record.TryGetQuicEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? v6);
        return (hasV4 || hasV6) && (!hasV4 || IsRelayable(v4!.Address)) && (!hasV6 || IsRelayable(v6!.Address));

        bool IsRelayable(IPAddress ip) => !ip.IsWildcardOrNone && !ip.IsMulticast && !ip.IsSpecialUseAddress &&
                                          (!ip.IsLoopbackOrPrivateOrLinkLocal || allowLocalRecords && IPAddress.IsLoopback(ip));
    }

    private sealed class BrowserCertificates : IDisposable
    {
        private static readonly TimeSpan _validity = TimeSpan.FromDays(14);
        private X509Certificate2 _next;

        public BrowserCertificates()
        {
            DateTimeOffset start = TimeProvider.System.GetUtcNow().AddMinutes(-1);
            Current = Create(start);
            try
            {
                _next = Create(start + _certificateRotation);
            }
            catch
            {
                Current.Dispose();
                throw;
            }
        }

        public X509Certificate2 Current { get; private set; }

        public byte[] GetHashes()
        {
            byte[] result = new byte[2 * SHA256.HashSizeInBytes];
            SHA256.HashData(Current.RawDataMemory.Span, result);
            SHA256.HashData(_next.RawDataMemory.Span, result.AsSpan(SHA256.HashSizeInBytes));
            return result;
        }

        public void Rotate(MsQuicListener listener)
        {
            DateTimeOffset now = TimeProvider.System.GetUtcNow();
            if (now < _next.NotBefore.ToUniversalTime() || now >= _next.NotAfter.ToUniversalTime())
                throw new InvalidOperationException("The next WebTransport certificate is outside its validity window.");

            X509Certificate2 following = Create(now + _certificateRotation - TimeSpan.FromMinutes(1));
            try
            {
                listener.UpdateCertificate(_next);
            }
            catch
            {
                following.Dispose();
                throw;
            }

            Current.Dispose();
            Current = _next;
            _next = following;
        }

        public void Dispose()
        {
            _next.Dispose();
            Current.Dispose();
        }

        private static X509Certificate2 Create(DateTimeOffset start)
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            CertificateRequest request = new("CN=devp2p", key, HashAlgorithmName.SHA256);
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new("1.3.6.1.5.5.7.3.1")], critical: false));
            return request.CreateSelfSigned(start, start + _validity);
        }
    }
}
