// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Test;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.SyncLimits;
using Nethermind.Xdc.P2P;
using Nethermind.Xdc.P2P.Messages;
using NUnit.Framework;

namespace Nethermind.Xdc.Test.Network;

/// <summary>
/// XDC relocates the three EIP-2464 messages to <c>0xe3</c>-<c>0xe5</c> but leaves their payloads untouched,
/// so each message must encode byte-for-byte like its upstream counterpart.
/// </summary>
/// <remarks>
/// Each message is serialized by whichever serializer production dispatches to, which for the announcement and
/// the response is the upstream one - serializer lookup binds the static type at the send site.
/// </remarks>
[TestFixture, Parallelizable(ParallelScope.All)]
public class XdcPooledTransactionMessagesTests
{
    private static readonly Hash256[] Hashes = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];
    private static readonly ValueHash256[] ValueHashes = [TestItem.KeccakA, TestItem.KeccakB, TestItem.KeccakC];

    [Test]
    public void NewPooledTransactionHashes_uses_relocated_code_and_upstream_payload()
    {
        using XdcNewPooledTransactionHashesMessage message = new(Hashes.ToPooledList());
        using NewPooledTransactionHashesMessage upstream = new(Hashes.ToPooledList());
        NewPooledTransactionHashesMessageSerializer serializer = new();

        Assert.That(message.PacketType, Is.EqualTo(XdcMessageCode.NewPooledTransactionHashes));
        Assert.That(Hex(buffer => serializer.Serialize(buffer, message)),
            Is.EqualTo(Hex(buffer => serializer.Serialize(buffer, upstream))));
    }

    [Test]
    public void GetPooledTransactions_uses_relocated_code_and_upstream_payload()
    {
        using XdcGetPooledTransactionsMessage message = new(ValueHashes.ToPooledList());
        using GetPooledTransactionsMessage upstream = new(ValueHashes.ToPooledList());

        Assert.That(message.PacketType, Is.EqualTo(XdcMessageCode.GetPooledTransactions));
        Assert.That(Hex(buffer => new XdcGetPooledTransactionsMessageSerializer().Serialize(buffer, message)),
            Is.EqualTo(Hex(buffer => new GetPooledTransactionsMessageSerializer().Serialize(buffer, upstream))));
    }

    [Test]
    public void PooledTransactions_uses_relocated_code_and_upstream_payload()
    {
        using XdcPooledTransactionsMessage message = new(Transactions());
        using PooledTransactionsMessage upstream = new(Transactions());
        PooledTransactionsMessageSerializer serializer = new();

        Assert.That(message.PacketType, Is.EqualTo(XdcMessageCode.PooledTransactions));
        Assert.That(Hex(buffer => serializer.Serialize(buffer, message)),
            Is.EqualTo(Hex(buffer => serializer.Serialize(buffer, upstream))));
    }

    [Test]
    public void Hashes_survive_a_roundtrip()
    {
        XdcGetPooledTransactionsMessageSerializer serializer = new();
        using XdcGetPooledTransactionsMessage message = new(ValueHashes.ToPooledList());

        IByteBuffer buffer = Unpooled.Buffer();
        serializer.Serialize(buffer, message);
        using XdcGetPooledTransactionsMessage deserialized = serializer.Deserialize(buffer);

        Assert.That(deserialized.Hashes.AsSpan().ToArray(), Is.EqualTo(ValueHashes));
        Assert.That(deserialized.PacketType, Is.EqualTo(XdcMessageCode.GetPooledTransactions));
    }

    [Test]
    public void GetPooledTransactions_limit_reports_concrete_message_type()
    {
        XdcGetPooledTransactionsMessageSerializer serializer = new();
        using XdcGetPooledTransactionsMessage message = new(new ValueHash256[NethermindSyncLimits.MaxHashesFetch + 1].ToPooledList());
        using DisposableByteBuffer buffer = Unpooled.Buffer().AsDisposable();
        serializer.Serialize(buffer, message);

        Assert.That(() => serializer.Deserialize(buffer),
            Throws.InstanceOf<RlpException>().With.Message.Contains(nameof(XdcGetPooledTransactionsMessage)));
    }

    private static ArrayPoolList<Transaction> Transactions() =>
        new(1) { Build.A.Transaction.SignedAndResolved().TestObject };

    private static string Hex(Action<IByteBuffer> serialize)
    {
        IByteBuffer buffer = Unpooled.Buffer();
        serialize(buffer);
        return buffer.ReadAllHex();
    }
}
