// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Net;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V68;

public class Eth68ProtocolHandlerTests
{
    private ISession _session;
    private IMessageSerializationService _svc;
    private ISyncServer _syncManager;
    private ITxPool _transactionPool;
    private IPooledTxsRequestor _pooledTxsRequestor;
    private IGossipPolicy _gossipPolicy;
    private ISpecProvider _specProvider;
    private Block _genesisBlock;
    private Eth68ProtocolHandler _handler;

    [SetUp]
    public void Setup()
    {
        _svc = Build.A.SerializationService().WithEth68().TestObject;

        NetworkDiagTracer.IsEnabled = true;

        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _syncManager = Substitute.For<ISyncServer>();
        _transactionPool = Substitute.For<ITxPool>();
        _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
        _specProvider = Substitute.For<ISpecProvider>();
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        ITimerFactory timerFactory = Substitute.For<ITimerFactory>();
        _handler = new Eth68ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(timerFactory, LimboLogs.Instance),
            _syncManager,
            _transactionPool,
            _pooledTxsRequestor,
            _gossipPolicy,
            new ForkInfo(_specProvider, _genesisBlock.Header.Hash),
            LimboLogs.Instance);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        _handler.ProtocolCode.Should().Be("eth");
        _handler.Name.Should().Be("eth68");
        _handler.ProtocolVersion.Should().Be(68);
        _handler.MessageIdSpaceSize.Should().Be(17);
        _handler.IncludeInTxPool.Should().BeTrue();
        _handler.ClientId.Should().Be(_session.Node?.ClientId);
        _handler.HeadHash.Should().BeNull();
        _handler.HeadNumber.Should().Be(0);
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(100)]
    public void Can_handle_NewPooledTransactions_message(int txCount)
    {
        GenerateLists(txCount, out List<byte> types, out List<int> sizes, out List<Keccak> hashes);

        var msg = new NewPooledTransactionHashesMessage68(types, sizes, hashes);

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);
        _pooledTxsRequestor.Received().RequestTransactionsEth66(Arg.Any<Action<GetPooledTransactionsMessage>>(),
            Arg.Any<IReadOnlyList<Keccak>>());
    }

    [TestCase(true)]
    [TestCase(false)]
    public void Should_throw_when_sizes_doesnt_match(bool removeSize)
    {
        GenerateLists(4, out List<byte> types, out List<int> sizes, out List<Keccak> hashes);

        if (removeSize)
        {
            sizes.RemoveAt(sizes.Count - 1);
        }
        else
        {
            types.RemoveAt(sizes.Count - 1);
        }

        var msg = new NewPooledTransactionHashesMessage68(types, sizes, hashes);

        HandleIncomingStatusMessage();
        Action action = () => HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);
        action.Should().Throw<SubprotocolException>();
    }

    [Test]
    public void Should_process_huge_transaction()
    {
        Transaction tx = Build.A.Transaction.WithType(TxType.EIP1559).WithData(new byte[2 * 1024 * 1024])
            .WithHash(TestItem.KeccakA).TestObject;

        TxDecoder txDecoder = new();

        var msg = new NewPooledTransactionHashesMessage68(new[] { (byte)tx.Type },
            new[] { txDecoder.GetLength(tx, RlpBehaviors.None) }, new[] { tx.Hash });

        HandleIncomingStatusMessage();

        HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);
        _pooledTxsRequestor.Received().RequestTransactionsEth66(Arg.Any<Action<GetPooledTransactionsMessage>>(),
            Arg.Any<IReadOnlyList<Keccak>>());
    }

    [TestCase(1)]
    [TestCase(NewPooledTransactionHashesMessage68.MaxCount - 1)]
    [TestCase(NewPooledTransactionHashesMessage68.MaxCount)]
    public void should_send_up_to_MaxCount_hashes_in_one_NewPooledTransactionHashesMessage68(int txCount)
    {
        Transaction[] txs = new Transaction[txCount];

        for (int i = 0; i < txCount; i++)
        {
            txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
        }

        _handler.SendNewTransactions(txs, false);

        _session.Received(1).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m => m.Hashes.Count == txCount));
    }

    [TestCase(NewPooledTransactionHashesMessage68.MaxCount - 1)]
    [TestCase(NewPooledTransactionHashesMessage68.MaxCount)]
    [TestCase(10000)]
    [TestCase(20000)]
    public void should_send_more_than_MaxCount_hashes_in_more_than_one_NewPooledTransactionHashesMessage68(int txCount)
    {
        int nonFullMsgTxsCount = txCount % NewPooledTransactionHashesMessage68.MaxCount;
        int messagesCount = txCount / NewPooledTransactionHashesMessage68.MaxCount + (nonFullMsgTxsCount > 0 ? 1 : 0);
        Transaction[] txs = new Transaction[txCount];

        for (int i = 0; i < txCount; i++)
        {
            txs[i] = Build.A.Transaction.SignedAndResolved().TestObject;
        }

        _handler.SendNewTransactions(txs, false);

        _session.Received(messagesCount).DeliverMessage(Arg.Is<NewPooledTransactionHashesMessage68>(m => m.Hashes.Count == NewPooledTransactionHashesMessage68.MaxCount || m.Hashes.Count == nonFullMsgTxsCount));
    }

    private void HandleIncomingStatusMessage()
    {
        var statusMsg = new StatusMessage();
        statusMsg.GenesisHash = _genesisBlock.Hash;
        statusMsg.BestHash = _genesisBlock.Hash;

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, byte messageCode) where T : MessageBase
    {
        IByteBuffer getBlockHeadersPacket = _svc.ZeroSerialize(msg);
        getBlockHeadersPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(getBlockHeadersPacket) { PacketType = messageCode });
    }

    private void GenerateLists(int txCount, out List<byte> types, out List<int> sizes, out List<Keccak> hashes)
    {
        TxDecoder txDecoder = new();
        types = new();
        sizes = new();
        hashes = new();

        for (int i = 0; i < txCount; ++i)
        {
            Transaction tx = Build.A.Transaction.WithType((TxType)(i % 3)).WithData(new byte[i])
                .WithHash(i % 2 == 0 ? TestItem.KeccakA : TestItem.KeccakB).TestObject;

            types.Add((byte)tx.Type);
            sizes.Add(txDecoder.GetLength(tx, RlpBehaviors.None));
            hashes.Add(tx.Hash);
        }
    }
}
