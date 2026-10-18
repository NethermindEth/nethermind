// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class PendingTransactionPayloadCacheTests
{
    [TestCase(true, TestName = "WithHash")]
    [TestCase(false, TestName = "WithoutHash")]
    public void Get_ForTheSameTransaction_SerializesItOnce(bool withHash)
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance);
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        if (!withHash) transaction.Hash = null;

        SerializedJson first = payloads.Get(transaction);
        SerializedJson second = payloads.Get(transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.Utf8Json, Is.SameAs(first.Utf8Json), "every subscriber must share one serialized transaction");
            Assert.That(Encoding.UTF8.GetString(first.Utf8Json), Is.EqualTo(new EthereumJsonSerializer().Serialize(
                TransactionForRpc.FromTransaction(transaction, new(MainnetSpecProvider.Instance.ChainId)))), "the shared payload is what a subscriber serialized on its own");
        }
    }

    [Test]
    public void Get_ForAnotherTransaction_SerializesItsOwnPayload()
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance);
        Transaction first = Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject;
        Transaction second = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;

        SerializedJson firstPayload = payloads.Get(first);

        Assert.That(payloads.Get(second).Utf8Json, Is.Not.EqualTo(firstPayload.Utf8Json));
    }

    [Test]
    public void RemoveSubscriber_ForTheLastSubscriber_DropsTheKeptTransactions()
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance);
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        payloads.AddSubscriber();
        payloads.AddSubscriber();
        SerializedJson first = payloads.Get(transaction);

        payloads.RemoveSubscriber();
        Assert.That(payloads.Get(transaction).Utf8Json, Is.SameAs(first.Utf8Json), "a remaining subscriber still shares the payload");

        payloads.RemoveSubscriber();
        payloads.AddSubscriber();
        Assert.That(payloads.Get(transaction).Utf8Json, Is.Not.SameAs(first.Utf8Json), "the last subscriber leaving cleared the table");
    }
}
