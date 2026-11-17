// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Core.Test.Encoding;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V71;

public class Eth71ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private IChainHeadSpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth71ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;

    [SetUp]
    public void Setup()
    {
        NetworkDiagTracer.IsEnabled = true;

        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _syncManager = Substitute.For<ISyncServer>();
        _transactionPool = Substitute.For<ITxPool>();
        _specProvider = Substitute.For<IChainHeadSpecProvider>();
        _syncManager.ServeReceipts(_specProvider);
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _syncManager.LowestBlock.Returns(0UL);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _svc = Build.A.SerializationService().WithEth71(_specProvider).TestObject;
        _handler = new Eth71ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(),
            _specProvider,
            _txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _session?.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(_session.DeliverMessage))
            .ForEach(c => (c.GetArguments()[0] as P2PMessage)?.Dispose());

        _handler.Dispose();
        _session.Dispose();
        _syncManager.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
            Assert.That(_handler.Name, Is.EqualTo("eth71"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(71));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(20));
            Assert.That(_handler.IncludeInTxPool, Is.True);
            Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
            Assert.That(_handler.HeadHash, Is.Null);
            Assert.That(_handler.HeadNumber, Is.EqualTo(0));
        }
    }

    [Test]
    public void Should_handle_eth70_GetReceipts_message()
    {
        TxReceipt[] receipts = [new() { GasUsedTotal = GasCostOf.Transaction, Logs = [] }];
        _syncManager.FindHeader(Keccak.Zero).Returns(_genesisBlock.Header);
        _syncManager.GetReceipts(Keccak.Zero).Returns(receipts);

        HandleIncomingStatusMessage();
        using GetReceiptsMessage70 request = new(1111, 0, new[] { Keccak.Zero }.ToPooledList());
        HandleZeroMessage(request, Eth70MessageCode.GetReceipts);

        _session.Received(1).DeliverMessage(Arg.Is<ReceiptsMessage70>(m =>
            m.RequestId == 1111 &&
            m.TxReceipts.Count == 1 &&
            m.TxReceipts[0].Length == 1 &&
            m.TxReceipts[0][0].GasUsedTotal == GasCostOf.Transaction &&
            !m.LastBlockIncomplete));
    }

    [TestCaseSource(nameof(BlockAccessListsResponseCases))]
    public void Should_handle_GetBlockAccessLists_and_return_expected_BALs(
        long requestId,
        Hash256[] hashes,
        byte[]?[] expectedBlockAccessLists)
    {
        BlockAccessListsMessage? response = null;
        _session.When(s => s.DeliverMessage(Arg.Any<BlockAccessListsMessage>())).Do(call =>
            response = (BlockAccessListsMessage)call[0]);

        for (int i = 0; i < hashes.Length; i++)
        {
            _syncManager.GetBlockAccessListRlp(hashes[i]).Returns(ArrayMemoryManager.From(expectedBlockAccessLists[i]));
        }

        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(requestId, hashes.ToPooledList(hashes.Length));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        _session.Received(1).DeliverMessage(Arg.Any<BlockAccessListsMessage>());
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Is.Not.Null);
            if (response is not null)
            {
                Assert.That(response.RequestId, Is.EqualTo(requestId));
                Assert.That(response.BlockAccessLists, Has.Count.EqualTo(expectedBlockAccessLists.Length));
                if (response.BlockAccessLists.Count == expectedBlockAccessLists.Length)
                {
                    for (int i = 0; i < expectedBlockAccessLists.Length; i++)
                    {
                        Assert.That(response.BlockAccessLists[i], Is.EqualTo(expectedBlockAccessLists[i]));
                    }
                }
            }
        }
    }

    [Test]
    public void Should_stop_BAL_response_after_soft_limit([Values] bool oversizedFirstEntry)
    {
        const int softLimit = 2 * MemorySizes.MiB;
        Hash256[] hashes = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];
        byte[][] entries =
        [
            RlpTestHelper.EncodeSequence([Rlp.Encode(new byte[oversizedFirstEntry ? softLimit + 1 : softLimit / 2 + 1])]),
            RlpTestHelper.EncodeSequence([Rlp.Encode(new byte[softLimit / 2])]),
            RlpTestHelper.EncodeSequence([Rlp.Encode(new byte[1024])])
        ];
        for (int i = 0; i < entries.Length; i++)
        {
            _syncManager.GetBlockAccessListRlp(hashes[i]).Returns(ArrayMemoryManager.From(entries[i]));
        }
        BlockAccessListsMessage? response = null;
        _session.When(s => s.DeliverMessage(Arg.Any<BlockAccessListsMessage>())).Do(call => response = (BlockAccessListsMessage)call[0]);
        HandleIncomingStatusMessage();
        using GetBlockAccessListsMessage request = new(333, hashes.ToPooledList(hashes.Length));

        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        Assert.That(response, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response!.RequestId, Is.EqualTo(333));
            Assert.That(response.BlockAccessLists, Has.Count.EqualTo(oversizedFirstEntry ? 1 : 2));
            Assert.That(response.BlockAccessLists[0], Is.EqualTo(entries[0]));
        }
    }

    [Test]
    public void Serves_full_BAL_response_as_the_stored_lists()
    {
        const int count = 20;
        Hash256[] hashes = [.. Enumerable.Range(0, count).Select(static i => Keccak.Compute(i.ToString()))];
        byte[]?[] entries = new byte[]?[count];
        for (int i = 0; i < count; i++)
        {
            if (i == 1)
            {
                _syncManager.GetBlockAccessListRlp(hashes[i]).Returns((MemoryManager<byte>?)null);
                continue;
            }

            byte[] payload = new byte[200_000 + i];
            new Random(i).NextBytes(payload);
            entries[i] = RlpTestHelper.EncodeSequence([Rlp.Encode(payload)]);
            _syncManager.GetBlockAccessListRlp(hashes[i]).Returns(ArrayMemoryManager.From(entries[i]!));
        }

        BlockAccessListsMessageSerializer serializer = new();
        byte[]? response = null;
        int servedCount = 0;
        _session.When(s => s.DeliverMessage(Arg.Any<BlockAccessListsMessage>())).Do(call =>
        {
            BlockAccessListsMessage message = (BlockAccessListsMessage)call[0];
            servedCount = message.BlockAccessLists.Count;
            response = new byte[serializer.GetLength(message)];
            serializer.Serialize(response, message);
        });
        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(1111, hashes.ToPooledList(count));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        using BlockAccessListsMessage expectedMessage = new(1111, entries[..servedCount].ToPooledList());
        byte[] expected = new byte[serializer.GetLength(expectedMessage)];
        serializer.Serialize(expected, expectedMessage);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(expected, Has.Length.GreaterThan(2 * MemorySizes.MiB));
            Assert.That(response, Is.EqualTo(expected));
        }
    }

    [TestCaseSource(nameof(BlockAccessListsRequestCases))]
    public async Task Can_request_and_handle_block_access_lists(bool viaSyncPeerInterface)
    {
        byte[] bal1 = [0xc1, 0x80];
        byte[] bal2 = [0xc2, 0x01, 0x02];

        BlockAccessListsMessage? response = null;
        GetBlockAccessListsMessage? sentRequest = null;

        _session.When(s => s.DeliverMessage(Arg.Any<GetBlockAccessListsMessage>())).Do(call =>
        {
            sentRequest = (GetBlockAccessListsMessage)call[0];
            byte[]?[] blockAccessLists = [bal1, bal2];
            response = new BlockAccessListsMessage(sentRequest.RequestId, new ArrayPoolList<byte[]?>(blockAccessLists));
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<byte[]?>> task = viaSyncPeerInterface
            ? ((ISyncPeer)_handler).GetBlockAccessLists(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None)
            : _handler.GetBlockAccessLists(new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        _session.Received(1).DeliverMessage(Arg.Any<GetBlockAccessListsMessage>());
        HandleZeroMessage(response!, Eth71MessageCode.BlockAccessLists);

        using IOwnedReadOnlyList<byte[]?> result = await task;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Count.EqualTo(2));
            if (result.Count == 2)
            {
                Assert.That(result[0], Is.EqualTo(bal1));
                Assert.That(result[1], Is.EqualTo(bal2));
            }
            Assert.Throws<ObjectDisposedException>(() => _ = sentRequest!.Hashes[0]);
        }
        response?.Dispose();
    }

    [Test]
    public void Should_throw_when_receiving_unrequested_block_access_lists()
    {
        using BlockAccessListsMessage msg = new(9999, IOwnedReadOnlyList<byte[]?>.Empty);

        HandleIncomingStatusMessage();
        Assert.Throws<SubprotocolException>(() => HandleZeroMessage(msg, Eth71MessageCode.BlockAccessLists));
    }

    [Test]
    public void Should_reject_unrequested_block_access_lists_before_decoding()
    {
        HandleIncomingStatusMessage();
        UndecodableResponse.AssertRejectedAsUnrequested(_handler.HandleMessage, Eth71MessageCode.BlockAccessLists);
    }

    [Test]
    public async Task Should_return_empty_list_when_requesting_zero_hashes()
    {
        HandleIncomingStatusMessage();
        IOwnedReadOnlyList<byte[]?> result = await _handler.GetBlockAccessLists(
            Array.Empty<Hash256>(), CancellationToken.None);

        Assert.That(result.Count, Is.EqualTo(0));
        result.Dispose();
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetBlockAccessListsMessage>());
    }

    [Test]
    public void Should_handle_empty_request()
    {
        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(4444,
            new ArrayPoolList<Hash256>(0));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        _session.Received(1).DeliverMessage(Arg.Is<BlockAccessListsMessage>(m =>
            m.RequestId == 4444 &&
            m.BlockAccessLists.Count == 0));
    }

    private void HandleIncomingStatusMessage()
    {
        using StatusMessage69 statusMsg = new()
        {
            ProtocolVersion = 71,
            GenesisHash = _genesisBlock.Hash!,
            LatestBlockHash = _genesisBlock.Hash!
        };

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new NettyBufferPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : P2PMessage
    {
        IByteBuffer packet = _svc.ZeroSerialize(msg);
        packet.ReadByte();
        _handler.HandleMessage(new NettyBufferPacket(packet) { PacketType = (byte)messageCode });
    }

    private static TestCaseData[] BlockAccessListsResponseCases =>
    [
        new TestCaseData(
                1111L,
                new[] { Keccak.Zero, TestItem.KeccakA },
                new byte[]?[] { [0xc1, 0x80], [0xc2, 0x01, 0x02] })
            .SetName("Should_handle_GetBlockAccessLists_and_return_available_BALs"),
        new TestCaseData(
                2222L,
                new[] { Keccak.Zero, TestItem.KeccakA },
                new byte[]?[] { null, null })
            .SetName("Should_return_absent_BAL_for_unavailable_blocks"),
        new TestCaseData(
                3333L,
                new[] { Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB },
                new byte[]?[] { [0xc3, 0x01, 0x02, 0x03], null, null })
            .SetName("Should_return_mixed_available_and_absent_BALs")
    ];

    private static TestCaseData[] BlockAccessListsRequestCases =>
    [
        new TestCaseData(false)
            .SetName("Can_request_and_handle_block_access_lists"),
        new TestCaseData(true)
            .SetName("Can_request_and_handle_block_access_lists_via_sync_peer_interface")
    ];

}
