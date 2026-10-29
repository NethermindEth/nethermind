// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Logging;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing a base p2p message into a packet the way the packet sender does, and deserializing it back the way the p2p protocol handler does.
/// </summary>
[MemoryDiagnoser]
public class P2PMessageBenchmarks
{
    public enum Message
    {
        Hello,
        Disconnect,
        Ping,
        AddCapability
    }

    private IContainer _container = null!;
    private Func<int> _serialize = null!;
    private Func<byte[], P2PMessage> _deserialize = null!;
    private byte[] _body = null!;

    [Params(Message.Hello, Message.Disconnect, Message.Ping, Message.AddCapability)]
    public Message Type { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.TestObject
        };
        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, new ConfigProvider(), LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(TestItem.PrivateKeyA, null))
            .Build();
        IMessageSerializationService service = _container.Resolve<IMessageSerializationService>();

        switch (Type)
        {
            case Message.Hello:
                SetUp(service, new HelloMessage
                {
                    P2PVersion = 5,
                    ClientId = "Nethermind/v1.36.0+00000000/linux-x64/dotnet10.0.0",
                    ListenPort = 30303,
                    NodeId = TestItem.PublicKeyA,
                    Capabilities = new ArrayPoolList<Capability>(4)
                    {
                        new(Protocol.Eth, 68),
                        new(Protocol.Eth, 69),
                        new(Protocol.Eth, 70),
                        new(Protocol.Snap, 1)
                    }
                });
                break;
            case Message.Disconnect:
                SetUp(service, new DisconnectMessage(EthDisconnectReason.TooManyPeers));
                break;
            case Message.Ping:
                SetUp(service, PingMessage.Instance);
                break;
            case Message.AddCapability:
                SetUp(service, new AddCapabilityMessage(new Capability(Protocol.Snap, 1)));
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container.Dispose();

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public int Deserialize()
    {
        using P2PMessage message = _deserialize(_body);
        return message.PacketType;
    }

    private void SetUp<T>(IMessageSerializationService service, T message) where T : P2PMessage
    {
        message.AdaptivePacketType = message.PacketType;
        _serialize = () =>
        {
            IByteBuffer packet = service.ZeroSerialize(message);
            int length = packet.ReadableBytes;
            packet.Release();
            return length;
        };
        _deserialize = body => service.Deserialize<T>(body);

        IByteBuffer packet = service.ZeroSerialize(message);
        packet.SkipBytes(Rlp.LengthOf(message.AdaptivePacketType));
        _body = packet.ReadAllBytesAsArray();
        packet.Release();
    }
}
