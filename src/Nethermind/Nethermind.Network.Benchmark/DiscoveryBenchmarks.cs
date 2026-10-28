// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Net;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv4.Messages;
using Nethermind.Network.Enr;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing a discv4 message into a packet and deserializing it back, the way the discovery handler does.
/// </summary>
/// <remarks>
/// Signing and public key recovery return fixed results, because they would otherwise dominate the measurements.
/// </remarks>
[MemoryDiagnoser]
public class DiscoveryBenchmarks
{
    public enum Message
    {
        Ping,
        Pong,
        FindNode,
        Neighbors,
        EnrRequest,
        EnrResponse
    }

    private const int NeighborCount = 12;
    private const long ExpirationTime = 1_900_000_000;

    private IContainer _container = null!;
    private Func<int> _serialize = null!;
    private Func<byte[], DiscoveryMsg> _deserialize = null!;
    private byte[] _packet = null!;

    [Params(Message.Ping, Message.Pong, Message.FindNode, Message.Neighbors, Message.EnrRequest, Message.EnrResponse)]
    public Message Type { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        PrivateKey key = TestItem.PrivateKeyA;
        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters(),
            Allocations = [],
            Genesis = Build.A.Block.TestObject
        };
        _container = new ContainerBuilder()
            .AddModule(new PseudoNethermindModule(chainSpec, new ConfigProvider(), LimboLogs.Instance))
            .AddModule(new TestEnvironmentModule(key, null))
            .AddSingleton<IEcdsa>(new FixedEcdsa(new Ecdsa().Sign(key, Keccak.Zero), key))
            .AddSingleton<INodeIdResolver>(new FixedNodeIdResolver(key.PublicKey))
            .Build();
        IMessageSerializationService service = _container.Resolve<IMessageSerializationService>();

        IPEndPoint farAddress = new(IPAddress.Parse("192.168.1.2"), 30303);
        IPEndPoint nearAddress = new(IPAddress.Parse("10.0.0.5"), 30303);
        switch (Type)
        {
            case Message.Ping:
                SetUp(service, new PingMsg(farAddress, ExpirationTime, nearAddress) { EnrSequence = 5 });
                break;
            case Message.Pong:
                SetUp(service, new PongMsg(farAddress, ExpirationTime, TestItem.KeccakA.ValueHash256, 5));
                break;
            case Message.FindNode:
                SetUp(service, new FindNodeMsg(farAddress, ExpirationTime, TestItem.PublicKeyB.Bytes));
                break;
            case Message.Neighbors:
                Node[] nodes = new Node[NeighborCount];
                for (int i = 0; i < nodes.Length; i++)
                {
                    nodes[i] = new Node(TestItem.PublicKeys[i], $"10.0.{i}.1", 30303 + i);
                }

                SetUp(service, new NeighborsMsg(farAddress, ExpirationTime, nodes));
                break;
            case Message.EnrRequest:
                SetUp(service, new EnrRequestMsg(farAddress, ExpirationTime));
                break;
            case Message.EnrResponse:
                NodeRecord nodeRecord = new() { EnrSequence = 5 };
                nodeRecord.SetEntry(new SecP256k1Entry(key.CompressedPublicKey));
                nodeRecord.SetEntry(new IpEntry(nearAddress.Address));
                nodeRecord.SetEntry(new TcpEntry(nearAddress.Port));
                nodeRecord.SetEntry(new UdpEntry(nearAddress.Port));
                new NodeRecordSigner(new Ecdsa(), key).Sign(nodeRecord);
                SetUp(service, new EnrResponseMsg(farAddress, nodeRecord, TestItem.KeccakA));
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container.Dispose();

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public DiscoveryMsg Deserialize() => _deserialize(_packet);

    private void SetUp<T>(IMessageSerializationService service, T message) where T : DiscoveryMsg
    {
        _serialize = () =>
        {
            IByteBuffer buffer = service.ZeroSerialize(message, NethermindBuffers.DiscoveryAllocator);
            int length = buffer.ReadableBytes;
            buffer.Release();
            return length;
        };
        _deserialize = packet =>
        {
            IByteBuffer buffer = Unpooled.WrappedBuffer(packet);
            try
            {
                return service.Deserialize<T>(buffer);
            }
            finally
            {
                buffer.Release();
            }
        };

        IByteBuffer packet = service.ZeroSerialize(message);
        _packet = packet.ReadAllBytesAsArray();
        packet.Release();
    }

    private sealed class FixedEcdsa(Signature signature, PrivateKey key) : IEcdsa
    {
        public Signature Sign(PrivateKey privateKey, in ValueHash256 message) => signature;

        public PublicKey RecoverPublicKey(Signature signature, in ValueHash256 message) => key.PublicKey;

        public CompressedPublicKey RecoverCompressedPublicKey(Signature signature, in ValueHash256 message) => key.CompressedPublicKey;
    }

    private sealed class FixedNodeIdResolver(PublicKey nodeId) : INodeIdResolver
    {
        public PublicKey GetNodeId(ReadOnlySpan<byte> signature, int recoveryId, Span<byte> typeAndData) => nodeId;
    }
}
