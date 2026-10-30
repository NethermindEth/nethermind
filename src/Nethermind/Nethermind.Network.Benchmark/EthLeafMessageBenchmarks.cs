// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using DotNetty.Buffers;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Network.Benchmarks;

/// <summary>
/// Measures serializing an eth message into a packet the way the packet sender does, and deserializing it back from a DotNetty buffer the way the eth protocol handlers do.
/// </summary>
[MemoryDiagnoser]
public class EthLeafMessageBenchmarks
{
    public enum Message
    {
        Status,
        NewBlockHashes,
        NewBlock
    }

    private IContainer _container = null!;
    private Func<int> _serialize = null!;
    private Func<IByteBuffer, P2PMessage> _deserialize = null!;
    private IByteBuffer _body = null!;

    [Params(Message.Status, Message.NewBlockHashes, Message.NewBlock)]
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
            case Message.Status:
                SetUp(service, new StatusMessage
                {
                    ProtocolVersion = 68,
                    NetworkId = 1,
                    TotalDifficulty = UInt256.Parse("58750003716598352816469"),
                    BestHash = TestItem.KeccakA,
                    GenesisHash = TestItem.KeccakB,
                    ForkId = new ForkId(0x9f3d2254, 0)
                });
                break;
            case Message.NewBlockHashes:
                SetUp(service, new NewBlockHashesMessage((TestItem.KeccakA, 21_000_000), (TestItem.KeccakB, 21_000_001)));
                break;
            case Message.NewBlock:
                Transaction[] transactions = new Transaction[100];
                for (int i = 0; i < transactions.Length; i++)
                    transactions[i] = Build.A.Transaction.WithNonce((ulong)i).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

                Block block = Build.A.Block.WithNumber(21_000_000).WithTransactions(transactions).TestObject;
                SetUp(service, new NewBlockMessage { Block = block, TotalDifficulty = block.Difficulty });
                break;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _body.Release();
        _container.Dispose();
    }

    [Benchmark]
    public int Serialize() => _serialize();

    [Benchmark]
    public int Deserialize()
    {
        _body.SetReaderIndex(0);
        using P2PMessage message = _deserialize(_body);
        return message.PacketType;
    }

    private void SetUp<T>(IMessageSerializationService service, T message) where T : P2PMessage
    {
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
        _body = Unpooled.WrappedBuffer(packet.ReadAllBytesAsArray());
        packet.Release();
    }
}
