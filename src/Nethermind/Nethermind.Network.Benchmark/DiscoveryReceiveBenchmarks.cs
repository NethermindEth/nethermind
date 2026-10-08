// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures the discovery receive path over loopback UDP, from the operating system socket to the protocol handler.
/// </summary>
/// <remarks>
/// The listener binds the dual-stack wildcard like the default configuration, so IPv4 senders arrive as IPv4-mapped
/// addresses and go through normalization. Allocations are counted for the whole process, so they include the
/// receive loop thread.
/// </remarks>
public class DiscoveryReceiveBenchmarks
{
    private const int DatagramsPerInvoke = 64;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private readonly byte[] _datagram = new byte[256];
    private readonly ManualResetEventSlim _allReceived = new();
    private DiscoveryConnectionsPool _pool = null!;
    private Socket _sender = null!;
    private SocketAddress _listenerAddress = null!;
    private int _remaining;

    [GlobalSetup]
    public void GlobalSetup()
    {
        // An unset listener address with a resolved IPv4 wildcard is what makes the default configuration bind [::].
        NetworkListenerState listenerState = new(
            new NetworkConfig(),
            new FixedIpResolver(new NetworkConfig { LocalIp = "0.0.0.0" }),
            LimboLogs.Instance);
        _pool = new DiscoveryConnectionsPool(
            LimboLogs.Instance.GetClassLogger<DiscoveryConnectionsPool>(),
            new DiscoveryConfig(),
            listenerState);
        IDatagramSocket socket = _pool.Bind(
            static address => new UdpDatagramSocket(CompositeDiscoveryApp.CreateDatagramSocket(address)),
            0,
            OnReceive);

        _sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _listenerAddress = new IPEndPoint(IPAddress.Loopback, socket.LocalEndpoint!.Port).Serialize();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _pool.StopAsync().GetAwaiter().GetResult();
        _sender.Dispose();
        _allReceived.Dispose();
    }

    [Benchmark(OperationsPerInvoke = DatagramsPerInvoke)]
    public void Receive()
    {
        _allReceived.Reset();
        Volatile.Write(ref _remaining, DatagramsPerInvoke);
        for (int i = 0; i < DatagramsPerInvoke; i++)
        {
            _sender.SendTo(_datagram, SocketFlags.None, _listenerAddress);
        }

        if (!_allReceived.Wait(ReceiveTimeout))
        {
            throw new TimeoutException($"Received {DatagramsPerInvoke - Volatile.Read(ref _remaining)} of {DatagramsPerInvoke} datagrams.");
        }
    }

    private void OnReceive(PooledUdpReceiveResult datagram)
    {
        datagram.Dispose();
        if (Interlocked.Decrement(ref _remaining) == 0)
        {
            _allReceived.Set();
        }
    }
}
