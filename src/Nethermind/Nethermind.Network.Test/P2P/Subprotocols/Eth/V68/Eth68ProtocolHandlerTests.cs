using System.Collections.Generic;
using System.Net;
using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
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
            _specProvider,
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

    [Test]
    public void Can_handle_NewPooledTransactions_messages()
    {
        Transaction tx = Build.A.Transaction.WithHash(TestItem.KeccakA).TestObject;

        TxDecoder txDecoder = new();
        IReadOnlyList<TxType> types = new[] {tx.Type};
        IReadOnlyList<int> sizes = new[] {txDecoder.GetLength(tx, RlpBehaviors.None)};
        IReadOnlyList<Keccak> hashes = new[] {tx.Hash};

        var msg = new NewPooledTransactionHashesMessage68(types, sizes, hashes);


        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth68MessageCode.NewPooledTransactionHashes);
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
}
