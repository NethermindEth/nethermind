// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Autofac;
using CkzgLib;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Stats.Model;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.MessageDecodingCompatibility;
using Eth62 = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Eth63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Eth65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Eth66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Eth68 = Nethermind.Network.P2P.Subprotocols.Eth.V68.Messages;
using Eth72 = Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;

namespace Nethermind.Network.Test.P2P;

[TestFixture, Parallelizable(ParallelScope.All)]
public class MessageDecodingCompatibilityTests
{
    private static readonly IContainer Container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();

    private static IMessageSerializationService Service => Container.Resolve<IMessageSerializationService>();

    [OneTimeTearDown]
    public void OneTimeTearDown() => Container.Dispose();

    /// <summary>
    /// A sample of each <see cref="P2PMessage"/> the core modules register an <see cref="IMessageSerializer{T}"/> for.
    /// </summary>
    public static IEnumerable<Sample> Samples()
    {
        // The handlers read the base p2p messages from a byte array, which never checked for unread bytes.
        yield return Sample.Of(static () => new HelloMessage { Capabilities = new ArrayPoolList<Capability>(1) { new(Protocol.Eth, 68) }, NodeId = TestItem.PublicKeyA }, acceptsTrailingByte: true);
        yield return Sample.Of(static () => new AddCapabilityMessage(new Capability(Protocol.Eth, 68)), acceptsTrailingByte: true);
        yield return Sample.Of(static () => new DisconnectMessage(EthDisconnectReason.TooManyPeers), acceptsTrailingByte: true);
        yield return Sample.Of(static () => PingMessage.Instance, acceptsTrailingByte: true);
        yield return Sample.Of(static () => PongMessage.Instance, acceptsTrailingByte: true);
        yield return Sample.Of(static () => new Eth62.StatusMessage { GenesisHash = TestItem.KeccakA, BestHash = TestItem.KeccakB, ForkId = new ForkId(0xfc64ec04, 1150000) });
        yield return Sample.Of(static () => new Eth62.NewBlockHashesMessage((TestItem.KeccakA, 1UL)));
        yield return Sample.Of(static () => new Eth62.NewBlockMessage { Block = Build.A.Block.WithTransactions(SignedTransaction()).TestObject, TotalDifficulty = 1 });
        yield return Sample.Of(static () => new Eth62.GetBlockHeadersMessage { StartBlockNumber = 1, MaxHeaders = 1 });
        yield return Sample.Of(static () => new Eth62.BlockHeadersMessage(Headers()));
        yield return Sample.Of(static () => new Eth62.GetBlockBodiesMessage(TestItem.KeccakA));
        yield return Sample.Of(static () => new Eth62.BlockBodiesMessage(Bodies()));
        yield return Sample.Of(static () => new Eth62.TransactionsMessage(Transactions()));
        yield return Sample.Of(static () => new Eth63.GetNodeDataMessage(Hashes()));
        yield return Sample.Of(static () => new Eth63.NodeDataMessage(NodeData()));
        yield return Sample.Of(static () => new Eth63.GetReceiptsMessage(Hashes()));
        yield return Sample.Of(static () => new Eth63.ReceiptsMessage(Receipts()));
        yield return Sample.Of(static () => new Eth65.GetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 }));
        yield return Sample.Of(static () => new Eth65.NewPooledTransactionHashesMessage(Hashes()));
        yield return Sample.Of(static () => new Eth65.PooledTransactionsMessage(Transactions()));
        yield return Sample.Of(static () => new Eth66.GetBlockHeadersMessage(1, new Eth62.GetBlockHeadersMessage { StartBlockHash = TestItem.KeccakA, MaxHeaders = 1 }));
        yield return Sample.Of(static () => new Eth66.BlockHeadersMessage(1, new Eth62.BlockHeadersMessage(Headers())));
        yield return Sample.Of(static () => new Eth66.GetBlockBodiesMessage(1, new Eth62.GetBlockBodiesMessage(TestItem.KeccakA)));
        yield return Sample.Of(static () => new Eth66.BlockBodiesMessage(1, new Eth62.BlockBodiesMessage(Bodies())));
        yield return Sample.Of(static () => new Eth66.GetNodeDataMessage(1, new Eth63.GetNodeDataMessage(Hashes())));
        yield return Sample.Of(static () => new Eth66.NodeDataMessage(1, new Eth63.NodeDataMessage(NodeData())));
        yield return Sample.Of(static () => new Eth66.GetReceiptsMessage(1, new Eth63.GetReceiptsMessage(Hashes())));
        yield return Sample.Of(static () => new Eth66.ReceiptsMessage(1, new Eth63.ReceiptsMessage(Receipts())));
        yield return Sample.Of(static () => new Eth66.GetPooledTransactionsMessage(new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 }));
        yield return Sample.Of(static () => new Eth66.PooledTransactionsMessage(1, new Eth65.PooledTransactionsMessage(Transactions())));
        yield return Sample.Of(static () => new ReceiptsMessage69(1, new ReceiptsInnerMessage69(Receipts())));
        yield return Sample.Of(static () => new GetReceiptsMessage70(1, 1, Hashes()));
        yield return Sample.Of(static () => new ReceiptsMessage70(1, Receipts(), lastBlockIncomplete: true));
        yield return Sample.Of(static () => new GetBlockAccessListsMessage(1, Hashes()));
        yield return Sample.Of(static () => new BlockAccessListsMessage(1, new ArrayPoolList<byte[]?>(2) { null, new byte[] { 0xc1, 0x01 } }));
        yield return Sample.Of(static () => new Eth68.NewPooledTransactionHashesMessage68(
            new ArrayPoolList<byte>(1) { (byte)TxType.EIP1559 }, new ArrayPoolList<int>(1) { 100 }, new ArrayPoolList<ValueHash256>(1) { TestItem.KeccakA.ValueHash256 }));
        yield return Sample.Of(static () => new StatusMessage69
        {
            ProtocolVersion = 69,
            NetworkId = 1,
            GenesisHash = TestItem.KeccakA,
            ForkId = new ForkId(0xfc64ec04, 1150000),
            LatestBlock = 1,
            LatestBlockHash = TestItem.KeccakB
        });
        yield return Sample.Of(static () => new BlockRangeUpdateMessage { LatestBlock = 1, LatestBlockHash = TestItem.KeccakA });
        yield return Sample.Of(static () => new Eth72.NewPooledTransactionHashesMessage72([(byte)TxType.Blob], [1024], [TestItem.KeccakA.ValueHash256], CellMask()));
        yield return Sample.Of(static () => new Eth72.GetCellsMessage72(1, [TestItem.KeccakA.ValueHash256], CellMask()));
        yield return Sample.Of(static () => new Eth72.CellsMessage72(1, [TestItem.KeccakA.ValueHash256], [[new byte[Ckzg.BytesPerCell], new byte[Ckzg.BytesPerCell]]], CellMask()));
    }

    [Test]
    public void Every_registered_span_serializer_has_a_sample() =>
        AssertSampled(Container.Resolve<IReadOnlyList<SerializerInfo>>(), Samples());

    [Test]
    public void Accepts_a_trailing_byte_only_where_the_handlers_always_did([ValueSource(nameof(Samples))] Sample sample, [Values] bool trailingByte) =>
        AssertTrailingByteHandling(Service, sample, trailingByte);

    private static IEnumerable<TestCaseData> Payloads()
    {
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> getBlockBodies66 = Deserializer<Eth66.GetBlockBodiesMessage>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> receipts66 = Deserializer<Eth66.ReceiptsMessage>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> receipts63 = Deserializer<Eth63.ReceiptsMessage>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> blockBodies62 = Deserializer<Eth62.BlockBodiesMessage>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> getBlockHeaders62 = Deserializer<Eth62.GetBlockHeadersMessage>();
        Func<IMessageSerializationService, IByteBuffer, P2PMessage> blockRangeUpdate69 = Deserializer<BlockRangeUpdateMessage>();

        yield return new TestCaseData(getBlockBodies66, "c201c0", true).SetName("eth66 envelope");
        yield return new TestCaseData(getBlockBodies66, "f8", false).SetName("eth66 truncated header");
        yield return new TestCaseData(getBlockBodies66, "c301c0", false).SetName("eth66 list longer than the payload");
        yield return new TestCaseData(getBlockBodies66, "c0", false).SetName("eth66 missing request id");
        yield return new TestCaseData(getBlockBodies66, "c301c000", false).SetName("eth66 unread byte inside the list");
        yield return new TestCaseData(getBlockBodies66, "c201c000", false).SetName("eth66 byte after the list");
        yield return new TestCaseData(receipts66, "c101", true).SetName("eth66 receipts without a message");
        yield return new TestCaseData(receipts63, "", true).SetName("eth63 empty receipts");
        yield return new TestCaseData(receipts63, "c0", true).SetName("eth63 empty receipts list");
        yield return new TestCaseData(receipts63, "c000", false).SetName("eth63 empty receipts list with a trailing byte");
        yield return new TestCaseData(blockBodies62, "c0c0", false).SetName("eth62 bodies counted past their list");
        yield return new TestCaseData(getBlockHeaders62, "c301018080", false).SetName("eth62 header request with a short list length");
        yield return new TestCaseData(blockRangeUpdate69, $"e20102a0{new string('0', 64)}", false).SetName("eth69 block range update with a short list length");
    }

    [TestCaseSource(nameof(Payloads))]
    public void Accepts_only_what_the_handlers_accept(Func<IMessageSerializationService, IByteBuffer, P2PMessage> deserialize, string payload, bool accepted) =>
        Assert.That(IsAccepted(Service, deserialize, Bytes.FromHexString(payload)), Is.EqualTo(accepted));

    private static ArrayPoolList<Hash256> Hashes() => new(1) { TestItem.KeccakA };

    private static ArrayPoolList<BlockHeader> Headers() => new(1) { Build.A.BlockHeader.TestObject };

    private static Block[] Bodies() => [Build.A.Block.WithTransactions(SignedTransaction()).TestObject];

    private static ArrayPoolList<Transaction> Transactions() => new(1) { SignedTransaction() };

    private static Transaction SignedTransaction() => Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static ByteArrayListAdapter NodeData() => new(new ArrayPoolList<byte[]>(1) { new byte[] { 0xde, 0xad } });

    private static ArrayPoolList<TxReceipt[]> Receipts() => new(1) { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject } };

    private static byte[] CellMask() => BlobCellMask.FromIndices([4, 9]).ToBytes();
}
