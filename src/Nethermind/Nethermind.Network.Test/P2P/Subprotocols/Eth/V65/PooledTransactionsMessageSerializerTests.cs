// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V65;

[TestFixture, Parallelizable(ParallelScope.All)]
public class PooledTransactionsMessageSerializerTests
{
    [Test]
    public void Roundtrip_init()
    {
        PooledTransactionsMessageSerializer serializer = new();
        Transaction transaction = new()
        {
            GasLimit = 10,
            GasPrice = 100,
            Data = new byte[] { 4, 5, 6 },
            Nonce = 1000,
            Signature = new Signature(1, 2, 27),
            To = null,
            Value = 10000
        };
        transaction.Hash = transaction.CalculateHash();
        transaction.SenderAddress = null;

        using PooledTransactionsMessage message = new(new ArrayPoolList<Transaction>(2) { transaction, transaction });
        SerializerTester.TestZero(serializer, message, "e2d08203e8640a80822710830405061b0102d08203e8640a80822710830405061b0102");
    }

    [Test]
    public void Roundtrip_call()
    {
        PooledTransactionsMessageSerializer serializer = new();
        Transaction transaction = new()
        {
            Data = new byte[] { 1, 2, 3 },
            GasLimit = 10,
            GasPrice = 100,
            Nonce = 1000,
            Signature = new Signature(1, 2, 27),
            To = TestItem.AddressA,
            Value = 10000
        };
        transaction.Hash = transaction.CalculateHash();
        transaction.SenderAddress = null;

        using PooledTransactionsMessage message = new(new ArrayPoolList<Transaction>(2) { transaction, transaction });
        SerializerTester.TestZero(serializer, message, "f84ae48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102e48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102");
    }

    [Test]
    public void Can_handle_empty()
    {
        PooledTransactionsMessageSerializer serializer = new();
        using PooledTransactionsMessage message = new(ArrayPoolList<Transaction>.Empty());

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void Empty_to_string()
    {
        using PooledTransactionsMessage message = new(ArrayPoolList<Transaction>.Empty());
        using PooledTransactionsMessage message2 = new(null);

        _ = message.ToString();
        _ = message2.ToString();
    }

    [TestCaseSource(nameof(GetTransactionMessages))]
    public void Should_pass_roundtrip(PooledTransactionsMessage transactionsMessage)
    {
        SerializerTester.TestZero(
            new PooledTransactionsMessageSerializer(),
            transactionsMessage,
            additionallyExcluding: (o) =>
                o.For(msg => msg.Transactions)
                    .Exclude(tx => tx.SenderAddress));
        transactionsMessage.Dispose();
    }

    [TestCaseSource(nameof(GetTransactionMessages))]
    public void Should_contain_network_form_tx_wrapper(PooledTransactionsMessage transactionsMessage)
    {
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 130);
        PooledTransactionsMessageSerializer serializer = new();
        serializer.Serialize(buffer, transactionsMessage);
        using PooledTransactionsMessage deserializedMessage = serializer.Deserialize(buffer);
        foreach (Transaction? tx in deserializedMessage.Transactions.Where(tx => tx.Type == TxType.Blob))
        {
            Assert.That(tx.NetworkWrapper, Is.Not.Null);
        }

        foreach (Transaction? tx in deserializedMessage.Transactions.Where(tx => tx.Type != TxType.Blob))
        {
            Assert.That(tx.NetworkWrapper, Is.Null);
        }
    }

    private static IEnumerable<PooledTransactionsMessage> GetTransactionMessages() =>
        TransactionsMessageSerializerTests.GetTransactions()
            .Select(txs => new PooledTransactionsMessage(txs.ToPooledList(3)));
}
