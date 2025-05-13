// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Specs;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [TestFixture, Parallelizable(ParallelScope.Self)]
    public class Eth63ProtocolHandlerTests
    {
        private Context _ctx = null!;
        private CompositeDisposable _disposables = null!;

        [SetUp]
        public void Setup()
        {
            _ctx = new();
            _disposables = new();
            _ctx.Session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().AddTo(_disposables));
        }

        [TearDown]
        public void TearDown()
        {
            _disposables.Dispose();
            _ctx.Session.Dispose();
        }

        [Test]
        public async Task Can_request_and_handle_receipts()
        {
            TxReceipt[][]? receipts = Enumerable.Repeat(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 100).ToArray(),
                1000).ToArray(); // TxReceipt[1000][100]

            using ReceiptsMessage receiptsMsg = new(receipts.ToPooledList());
            Packet receiptsPacket =
                new("eth", Eth63MessageCode.Receipts, _ctx._receiptMessageSerializer.Serialize(receiptsMsg));

            Task<IOwnedReadOnlyList<TxReceipt[]>> task = _ctx.ProtocolHandler.GetReceipts(
                Enumerable.Repeat(Keccak.Zero, 1000).ToArray(),
                CancellationToken.None);

            _ctx.ProtocolHandler.HandleMessage(receiptsPacket);

            using var result = await task;
            result.Should().HaveCount(1000);
        }

        [Test]
        public async Task Limit_receipt_request()
        {
            TxReceipt[] oneBlockReceipt = Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 100).ToArray();
            Packet smallReceiptsPacket =
                new("eth", Eth63MessageCode.Receipts, _ctx._receiptMessageSerializer.Serialize(
                    new(RepeatPooled(oneBlockReceipt, 10))
                ));
            Packet largeReceiptsPacket =
                new("eth", Eth63MessageCode.Receipts, _ctx._receiptMessageSerializer.Serialize(
                    new(RepeatPooled(oneBlockReceipt, 1000))
                ));

            GetReceiptsMessage? receiptsMessage = null;

            _ctx.Session
                .When(session => session.DeliverMessage(Arg.Any<GetReceiptsMessage>()))
                .Do(info => receiptsMessage = (GetReceiptsMessage)info[0]);

            Task<IOwnedReadOnlyList<TxReceipt[]>> receiptsTask = _ctx.ProtocolHandler
                .GetReceipts(RepeatPooled(Keccak.Zero, 1000), CancellationToken.None)
                .AddResultTo(_disposables);

            _ctx.ProtocolHandler.HandleMessage(smallReceiptsPacket);
            await receiptsTask;

            Assert.That(receiptsMessage?.Hashes?.Count, Is.EqualTo(8));

            receiptsTask = _ctx.ProtocolHandler
                .GetReceipts(RepeatPooled(Keccak.Zero, 1000), CancellationToken.None)
                .AddResultTo(_disposables);

            _ctx.ProtocolHandler.HandleMessage(largeReceiptsPacket);
            await receiptsTask;

            Assert.That(receiptsMessage?.Hashes?.Count, Is.EqualTo(12));

            // Back to 10
            receiptsTask = _ctx.ProtocolHandler
                .GetReceipts(RepeatPooled(Keccak.Zero, 1000), CancellationToken.None)
                .AddResultTo(_disposables);

            _ctx.ProtocolHandler.HandleMessage(smallReceiptsPacket);
            await receiptsTask;

            Assert.That(receiptsMessage?.Hashes?.Count, Is.EqualTo(8));
            receiptsMessage.Dispose();
        }

        private ArrayPoolList<T> RepeatPooled<T>(T txReceipts, int count) =>
            Enumerable.Repeat(txReceipts, count).ToPooledList(count).AddTo(_disposables);

        [Test]
        public void Will_not_serve_receipts_requests_above_512()
        {
            using GetReceiptsMessage getReceiptsMessage = new(
                RepeatPooled(Keccak.Zero, 513));
            Packet getReceiptsPacket =
                new("eth", Eth63MessageCode.GetReceipts, _ctx._getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            _ctx.ProtocolHandler.HandleMessage(getReceiptsPacket);
            _ctx.Session.Received().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }

        [Test]
        public void Will_not_send_messages_larger_than_2MB()
        {
            _ctx.SyncServer.GetReceipts(Arg.Any<Hash256>()).Returns(
                Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 512).ToArray());

            using GetReceiptsMessage getReceiptsMessage = new(
                RepeatPooled(Keccak.Zero, 512));
            Packet getReceiptsPacket =
                new("eth", Eth63MessageCode.GetReceipts, _ctx._getReceiptMessageSerializer.Serialize(getReceiptsMessage));

            _ctx.ProtocolHandler.HandleMessage(getReceiptsPacket);
            _ctx.Session.Received().DeliverMessage(Arg.Is<ReceiptsMessage>(static r => r.TxReceipts.Count == 14));
        }

        private class Context
        {
            private readonly MessageSerializationService _serializationService;
            private readonly StatusMessageSerializer _statusMessageSerializer = new();
            public readonly ReceiptsMessageSerializer _receiptMessageSerializer = new(MainnetSpecProvider.Instance);
            public readonly GetReceiptsMessageSerializer _getReceiptMessageSerializer = new();

            public ISession Session { get; }

            private ISyncServer? _syncServer;
            public ISyncServer SyncServer => _syncServer ??= Substitute.For<ISyncServer>();

            private Eth63ProtocolHandler? _protocolHandler;
            public Eth63ProtocolHandler ProtocolHandler
            {
                get
                {
                    if (_protocolHandler is not null)
                    {
                        return _protocolHandler;
                    }

                    var nodeStatsManager = Substitute.For<INodeStatsManager>();
                    nodeStatsManager.GetOrAdd(Arg.Any<Node>()).Returns((c) => new NodeStatsLight((Node)c[0]));

                    _protocolHandler = new Eth63ProtocolHandler(
                        Session,
                        _serializationService,
                        nodeStatsManager,
                        SyncServer,
                        RunImmediatelyScheduler.Instance,
                        Substitute.For<ITxPool>(),
                        Substitute.For<IGossipPolicy>(),
                        LimboLogs.Instance);

                    StatusMessage statusMessage = new();
                    Packet statusPacket =
                        new("eth", Eth62MessageCode.Status, _statusMessageSerializer.Serialize(statusMessage));
                    _protocolHandler.HandleMessage(statusPacket);

                    return _protocolHandler;
                }
            }

            public Context()
            {
                Session = Substitute.For<ISession>();
                Session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
                NetworkDiagTracer.IsEnabled = true;

                _serializationService = new MessageSerializationService(
                    SerializerInfo.Create(_statusMessageSerializer),
                    SerializerInfo.Create(_receiptMessageSerializer),
                    SerializerInfo.Create(_getReceiptMessageSerializer)
                );
            }
        }
    }
}
