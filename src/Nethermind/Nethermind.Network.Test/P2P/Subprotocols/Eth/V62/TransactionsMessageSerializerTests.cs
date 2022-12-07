// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62;

[TestFixture, Parallelizable(ParallelScope.All)]
public class TransactionsMessageSerializerTests
{
    [Test]
    public void Roundtrip_init()
    {
        TransactionsMessageSerializer serializer = new();
        Transaction transaction = new();
        transaction.GasLimit = 10;
        transaction.GasPrice = 100;
        transaction.Data = new byte[] { 4, 5, 6 };
        transaction.Nonce = 1000;
        transaction.Signature = new Signature(1, 2, 27);
        transaction.To = null;
        transaction.Value = 10000;
        transaction.Hash = transaction.CalculateHash();
        transaction.SenderAddress = null;

        TransactionsMessage message = new(new[] { transaction, transaction });
        SerializerTester.TestZero(serializer, message,
            "e2d08203e8640a80822710830405061b0102d08203e8640a80822710830405061b0102");
    }

    [Test]
    public void Roundtrip_call()
    {
        TransactionsMessageSerializer serializer = new();
        Transaction transaction = new();
        transaction.Data = new byte[] { 1, 2, 3 };
        transaction.GasLimit = 10;
        transaction.GasPrice = 100;
        transaction.Nonce = 1000;
        transaction.Signature = new Signature(1, 2, 27);
        transaction.To = TestItem.AddressA;
        transaction.Value = 10000;
        transaction.Hash = transaction.CalculateHash();
        transaction.SenderAddress = null;

        TransactionsMessage message = new(new[] { transaction, transaction });
        SerializerTester.TestZero(serializer, message,
            "f84ae48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102e48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102");
    }

    [Test]
    public void Can_handle_empty()
    {
        TransactionsMessageSerializer serializer = new();
        TransactionsMessage message = new(new Transaction[] { });

        SerializerTester.TestZero(serializer, message);
    }

    [Test]
    public void To_string_empty()
    {
        TransactionsMessage message = new(new Transaction[] { });
        TransactionsMessage message2 = new(null);

        _ = message.ToString();
        _ = message2.ToString();
    }

    [TestCaseSource(nameof(GetTransactionMessages))]
    public void Should_pass_roundtrip(TransactionsMessage transactionsMessage) => SerializerTester.TestZero(
        new TransactionsMessageSerializer(),
        transactionsMessage,
        additionallyExcluding: (o) =>
            o.For(msg => msg.Transactions)
                .Exclude(tx => tx.SenderAddress));

    [TestCaseSource(nameof(GetTransactionMessages))]
    public void Should_contain_network_form_tx_wrapper(TransactionsMessage transactionsMessage)
    {
        IByteBuffer buffer = PooledByteBufferAllocator.Default.Buffer(1024 * 130);
        TransactionsMessageSerializer serializer = new();
        serializer.Serialize(buffer, transactionsMessage);
        TransactionsMessage deserializedMessage = serializer.Deserialize(buffer);
        foreach (Transaction? tx in deserializedMessage.Transactions.Where(tx => tx.Type == TxType.Blob))
        {
            Assert.That(tx.NetworkWrapper, Is.Not.Null);
        }

        foreach (Transaction? tx in deserializedMessage.Transactions.Where(tx => tx.Type != TxType.Blob))
        {
            Assert.That(tx.NetworkWrapper, Is.Null);
        }
    }

    private static IEnumerable<TransactionsMessage> GetTransactionMessages() =>
        GetTransactions().Select(txs => new TransactionsMessage(txs.ToList()));

    public static IEnumerable<IEnumerable<Transaction>> GetTransactions()
    {
        // simple transaction
        yield return new List<Transaction>
        {
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject
        };

        // several transactions combined with ssz ones
        yield return new List<Transaction>
        {
            Build.A.Transaction
                .WithType(TxType.Legacy)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(1)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithType(TxType.AccessList)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(2)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithTo(TestItem.AddressA)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(3)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithType(TxType.EIP1559)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
        };

        // several shard blob transactions
        yield return new List<Transaction>
        {
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(1)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject,
            Build.A.Transaction
                .WithTo(TestItem.AddressA)
                .WithShardBlobTxTypeAndFields(2)
                .SignedAndResolved(new EthereumEcdsa(BlockchainIds.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA)
                .TestObject
        };
    }
}
