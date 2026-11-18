// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Autofac;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Config;
using Nethermind.Network.Discovery;
using Nethermind.Network.Discovery.Discv4;
using Nethermind.Stats.Model;
using NSubstitute;

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
    public enum Classification
    {
        Bypass,
        UnsupportedType,
        CandidateMdc
    }

    [Params(256, 1280)]
    public int PacketSize { get; set; }

    [Params(Classification.Bypass, Classification.UnsupportedType, Classification.CandidateMdc)]
    public Classification Path { get; set; }

    private const int DatagramsPerInvoke = 64;
    private static readonly TimeSpan ReceiveTimeout = TimeSpan.FromSeconds(10);

    private byte[] _datagram = null!;
    private readonly ManualResetEventSlim _allReceived = new();
    private DiscoveryConnectionsPool _pool = null!;
    private Socket _sender = null!;
    private SocketAddress _listenerAddress = null!;
    private int _remaining;
    private IContainer _container = null!;
    private DiscoveryHandler _handler = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _datagram = new byte[PacketSize];
        _datagram[97] = Path == Classification.CandidateMdc ? (byte)1 : (byte)0;
        TestLogManager logManager = new(LogLevel.Error);
        _container = new ContainerBuilder()
            .AddModule(new Discovery.Discv4.Kademlia.KademliaModule(new Node(TestItem.PublicKeyA, IPEndPoint.Parse("127.0.0.1:30303")), []))
            .AddSingleton<ILogManager>(logManager)
            .AddSingleton<ITimestamper>(Timestamper.Default)
            .AddSingleton<IDiscoveryMsgListener>(Substitute.For<IDiscoveryMsgListener>())
            .AddSingleton<IMessageSerializationService>(Substitute.For<IMessageSerializationService>())
            .Build();
        _handler = _container.Resolve<DiscoveryHandler>();
        // An unset listener address with a resolved IPv4 wildcard is what makes the default configuration bind [::].
        NetworkListenerState listenerState = new(
            new NetworkConfig(),
            new FixedIpResolver(new NetworkConfig { LocalIp = "0.0.0.0" }),
            logManager);
        _pool = new DiscoveryConnectionsPool(
            logManager.GetClassLogger<DiscoveryConnectionsPool>(),
            new DiscoveryConfig(),
            listenerState);
        IDatagramSocket socket = _pool.Bind(
            static address => new UdpDatagramSocket(CompositeDiscoveryApp.CreateDatagramSocket(address)),
            0,
            Path == Classification.Bypass ? OnReceive : _handler.Receive);
        _handler.InitializeChannel(socket, OnReceive);

        _sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _listenerAddress = new IPEndPoint(IPAddress.Loopback, socket.LocalEndpoint!.Port).Serialize();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _pool.StopAsync().GetAwaiter().GetResult();
        _sender.Dispose();
        _allReceived.Dispose();
        _handler.CloseInbound();
        _container.Dispose();
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
