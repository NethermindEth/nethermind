// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
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
    private const int TokenLength = 32;
    private readonly BrowserProtocol _protocol = new(ecdsa, nodeKey, txPool);
    private readonly ILogger _logger = logManager.GetClassLogger<BrowserWebTransport>();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Lock _peerCacheLock = new();
    private Node[] _cachedPeers = [];
    private long _peerCacheTimestamp;
    private Task? _runTask;

    public async Task Execute(CancellationToken cancellationToken)
    {
        int port = config.WebTransportPort;
        if (port <= 0 || port > ushort.MaxValue)
            throw new InvalidOperationException("Network.WebTransportPort must be a UDP port between 1 and 65535, or 0 to disable it.");
        if (port == config.DiscoveryPort && listenerState.DiscoveryAddress is not null)
            throw new InvalidOperationException("Network.WebTransportPort and Network.DiscoveryPort must use different UDP ports.");

        IPAddress address = listenerState.RlpxAddress ?? listenerState.DiscoveryAddress ?? listenerState.PreferredAddress;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _runTask = RunAsync(new IPEndPoint(address, port), ready, _shutdown.Token);
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch
        {
            await StopAsync();
            throw;
        }
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

    private async Task RunAsync(IPEndPoint endpoint, TaskCompletionSource ready, CancellationToken cancellationToken)
    {
        string executable = OperatingSystem.IsWindows() ? "nethermind-webtransport.exe" : "nethermind-webtransport";
        string executablePath = Path.Combine(AppContext.BaseDirectory, executable);
        using TcpListener backend = new(IPAddress.Loopback, 0);
        using Process companion = new()
        {
            StartInfo = new ProcessStartInfo(executablePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
        };
        using CancellationTokenSource workersCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task workers = Task.CompletedTask;
        bool started = false;
        try
        {
            if (!File.Exists(executablePath))
                throw new FileNotFoundException($"The WebTransport companion must be installed next to Nethermind as {executable}.", executablePath);

            backend.Start(WorkerCount);
            started = companion.Start();
            byte[] token = RandomNumberGenerator.GetBytes(TokenLength);
            string settings = JsonSerializer.Serialize(new
            {
                listen = endpoint.ToString(),
                backend = backend.LocalEndpoint.ToString(),
                token = Convert.ToHexString(token),
            });
            await companion.StandardInput.WriteLineAsync(settings.AsMemory(), cancellationToken);
            await companion.StandardInput.FlushAsync(cancellationToken);

            string? update = await companion.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new IOException($"The WebTransport companion exited before binding UDP {endpoint}.");
            PublishBinding(endpoint, update);
            Task[] backendWorkers = new Task[WorkerCount];
            for (int i = 0; i < backendWorkers.Length; i++)
                backendWorkers[i] = ServeBackendAsync(backend, token, workersCancellation.Token);
            workers = Task.WhenAll(backendWorkers);
            if (_logger.IsInfo) _logger.Info($"Browser WebTransport listening on UDP {endpoint:hide}.");
            ready.SetResult();

            while ((update = await companion.StandardOutput.ReadLineAsync(cancellationToken)) is not null)
                PublishBinding(endpoint, update);
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException("The WebTransport companion stopped unexpectedly.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ready.TrySetCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            if (!ready.TrySetException(exception) && !cancellationToken.IsCancellationRequested && _logger.IsError)
                _logger.Error("Browser WebTransport stopped.", exception);
        }
        finally
        {
            listenerState.SetWebTransport(null);
            await workersCancellation.CancelAsync();
            if (started)
                await StopCompanionAsync(companion);
            await workers;
        }
    }

    private void PublishBinding(IPEndPoint expected, string update)
    {
        using JsonDocument message = JsonDocument.Parse(update);
        JsonElement root = message.RootElement;
        if (!IPEndPoint.TryParse(root.GetProperty("address").GetString()!, out IPEndPoint? bound) || !bound.Equals(expected))
            throw new InvalidDataException("The WebTransport companion reported a different bind endpoint.");
        string? hashes = root.GetProperty("hashes").GetString();
        if (hashes is not { Length: 128 })
            throw new InvalidDataException("The WebTransport companion did not report two SHA-256 certificate hashes.");
        listenerState.SetWebTransport(new(bound.Address, bound.Port, Convert.FromHexString(hashes)));
    }

    private static async Task StopCompanionAsync(Process companion)
    {
        companion.StandardInput.Close();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
        try
        {
            await companion.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                companion.Kill();
            }
            catch (InvalidOperationException) when (companion.HasExited)
            {
                // The process exited after the timeout but before Kill.
            }
            await companion.WaitForExitAsync();
        }
    }

    private async Task ServeBackendAsync(TcpListener listener, byte[] token, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
                client.NoDelay = true;
                using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                await ServeSessionAsync(client.GetStream(), token, deadline.Token);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException or RlpReaderException)
            {
                if (_logger.IsTrace) _logger.Trace($"Browser WebTransport request ended: {exception.Message}");
            }
            catch (Exception exception)
            {
                if (_logger.IsError) _logger.Error("Browser WebTransport request failed.", exception);
            }
        }
    }

    private async Task ServeSessionAsync(NetworkStream stream, byte[] token, CancellationToken cancellationToken)
    {
        byte[] prelude = new byte[TokenLength + 1 + BrowserProtocol.NonceLength];
        await stream.ReadExactlyAsync(prelude, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(token, prelude.AsSpan(0, TokenLength)))
            return;
        byte mode = prelude[TokenLength];
        if (mode is not (BrowserProtocol.TxMode or BrowserProtocol.DiscoveryMode))
            return;

        byte[] lengthPrefix = new byte[sizeof(int)];
        await WriteBlockAsync(stream, lengthPrefix, _protocol.CreateHandshake(mode, prelude.AsSpan(TokenLength + 1)), cancellationToken);
        await stream.ReadExactlyAsync(lengthPrefix, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
        byte[] result;
        if (mode == BrowserProtocol.TxMode)
        {
            if (length is <= 0 or > BrowserProtocol.MaxSubmitSize)
                return;
            using IMemoryOwner<byte> buffer = MemoryPool<byte>.Shared.Rent(length);
            Memory<byte> payload = buffer.Memory[..length];
            await stream.ReadExactlyAsync(payload, cancellationToken);
            result = _protocol.Submit(payload.Span, cancellationToken);
        }
        else
        {
            if (length != 1)
                return;
            await stream.ReadExactlyAsync(lengthPrefix.AsMemory(0, 1), cancellationToken);
            if (lengthPrefix[0] != 0xc0)
                return;
            result = await GetPeersAsync(cancellationToken);
        }

        await WriteBlockAsync(stream, lengthPrefix, result, cancellationToken);
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

    private static async ValueTask WriteBlockAsync(NetworkStream stream, byte[] prefix, byte[] payload, CancellationToken cancellationToken)
    {
        BinaryPrimitives.WriteInt32BigEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }
}
