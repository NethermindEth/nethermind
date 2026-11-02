// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V70.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using NUnit.Framework;
using Eth62 = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Eth63 = Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Eth65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Eth66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth;

/// <summary>
/// Pins which payloads the eth protocol handlers accept, as decoded through <see cref="IMessageSerializationService"/>.
/// </summary>
[TestFixture, Parallelizable(ParallelScope.All)]
public class EthMessageDecodingCompatibilityTests
{
    private static readonly IMessageSerializationService Service = new SerializationBuilder()
        .WithEth71(MainnetSpecProvider.Instance)
        .With(new Eth63.GetNodeDataMessageSerializer())
        .With(new Eth63.NodeDataMessageSerializer())
        .With(new Eth63.GetReceiptsMessageSerializer())
        .With(new Eth63.ReceiptsMessageSerializer(MainnetSpecProvider.Instance))
        .TestObject;

    public sealed record Sample(string Name, Func<byte[]> Payload, Func<IByteBuffer, P2PMessage> Deserialize)
    {
        public static Sample Of<T>(Func<T> create) where T : P2PMessage =>
            new($"{typeof(T).Namespace!.Split('.')[^2]}.{typeof(T).Name}", () => Serialize(create()), Deserializer<T>());

        public override string ToString() => Name;

        private static byte[] Serialize<T>(T message) where T : P2PMessage
        {
            using (message)
            {
                using DisposableByteBuffer packet = Service.ZeroSerialize(message).AsDisposable();
                packet.SkipBytes(Rlp.LengthOf(message.AdaptivePacketType));
                return packet.ReadAllBytesAsArray();
            }
        }
    }

    private static Func<IByteBuffer, P2PMessage> Deserializer<T>() where T : P2PMessage => static buffer => Service.Deserialize<T>(buffer);

    private static IEnumerable<Sample> Samples()
    {
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
    }

    [Test]
    public void Accepts_a_message_only_without_a_trailing_byte([ValueSource(nameof(Samples))] Sample sample, [Values] bool trailingByte)
    {
        byte[] payload = sample.Payload();
        if (trailingByte) payload = [.. payload, 0];

        Assert.That(IsAccepted(sample.Deserialize, payload), Is.EqualTo(!trailingByte));
    }

    private static IEnumerable<TestCaseData> Payloads()
    {
        Func<IByteBuffer, P2PMessage> getBlockBodies66 = Deserializer<Eth66.GetBlockBodiesMessage>();
        Func<IByteBuffer, P2PMessage> receipts66 = Deserializer<Eth66.ReceiptsMessage>();
        Func<IByteBuffer, P2PMessage> receipts63 = Deserializer<Eth63.ReceiptsMessage>();
        Func<IByteBuffer, P2PMessage> blockBodies62 = Deserializer<Eth62.BlockBodiesMessage>();
        Func<IByteBuffer, P2PMessage> getBlockHeaders62 = Deserializer<Eth62.GetBlockHeadersMessage>();

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
        yield return new TestCaseData(blockBodies62, "c0c0", true).SetName("eth62 bodies counted past their list");
        yield return new TestCaseData(getBlockHeaders62, "c301018080", true).SetName("eth62 header request with a short list length");
    }

    [TestCaseSource(nameof(Payloads))]
    public void Accepts_only_what_the_handlers_accept(Func<IByteBuffer, P2PMessage> deserialize, string payload, bool accepted) =>
        Assert.That(IsAccepted(deserialize, Bytes.FromHexString(payload)), Is.EqualTo(accepted));

    /// <summary>
    /// Mirrors <c>ProtocolHandlerBase.Deserialize</c>, which accepts a message only if decoding it reads the whole payload.
    /// </summary>
    private static bool IsAccepted(Func<IByteBuffer, P2PMessage> deserialize, byte[] payload)
    {
        IByteBuffer buffer = Unpooled.Buffer(payload.Length);
        buffer.WriteBytes(payload);
        try
        {
            using P2PMessage message = deserialize(buffer);
            return !buffer.IsReadable();
        }
        catch (Exception exception) when (exception is not AssertionException)
        {
            return false;
        }
        finally
        {
            buffer.Release();
        }
    }

    private static ArrayPoolList<Hash256> Hashes() => new(1) { TestItem.KeccakA };

    private static ArrayPoolList<BlockHeader> Headers() => new(1) { Build.A.BlockHeader.TestObject };

    private static Block[] Bodies() => [Build.A.Block.WithTransactions(SignedTransaction()).TestObject];

    private static ArrayPoolList<Transaction> Transactions() => new(1) { SignedTransaction() };

    private static Transaction SignedTransaction() => Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject;

    private static ByteArrayListAdapter NodeData() => new(new ArrayPoolList<byte[]>(1) { new byte[] { 0xde, 0xad } });

    private static ArrayPoolList<TxReceipt[]> Receipts() => new(1) { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject } };
}
