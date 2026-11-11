// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc.Modules.Subscribe;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

[Parallelizable(ParallelScope.All)]
public class PendingTransactionPayloadCacheTests
{
    [TestCase(true, TestName = "WithHash")]
    [TestCase(false, TestName = "WithoutHash")]
    public void Get_ForTheSameTransaction_SerializesItOnceWhenItHasAHash(bool withHash)
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance, Substitute.For<IBlockFinder>(), 12);
        payloads.AddSubscriber();
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        if (!withHash) transaction.Hash = null;

        SerializedJson first = payloads.Get(transaction);
        SerializedJson second = payloads.Get(transaction);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(ReferenceEquals(second.Utf8Json, first.Utf8Json), Is.EqualTo(withHash), "subscribers share one serialized transaction; a transaction without a hash is not kept");
            Assert.That(second.Utf8Json, Is.EqualTo(first.Utf8Json));
            Assert.That(Encoding.UTF8.GetString(first.Utf8Json), Is.EqualTo(new EthereumJsonSerializer().Serialize(
                TransactionForRpc.FromTransaction(transaction, new(MainnetSpecProvider.Instance.ChainId)))), "the shared payload is what a subscriber serialized on its own");
        }
    }

    [Test]
    public void Get_ForAFrameTransactionBeforeEip8141_ReportsTheGasItHasOnceIncluded()
    {
        Transaction tx = FrameTxTestFrames.FrameTx(TestItem.AddressA, [], FrameTxTestFrames.SelfVerify(100_000), FrameTxTestFrames.Pay(100_000));
        tx.GasLimit = 200_000;
        tx.Hash = TestItem.KeccakA;
        ISpecProvider specProvider = new CustomSpecProvider(((ForkActivation)0, Prague.Instance), (new ForkActivation(0, 12), Eip8141Prototype.Instance));
        IBlockFinder blockFinder = Substitute.For<IBlockFinder>();
        blockFinder.Head.Returns(Build.A.Block.WithNumber(0).WithTimestamp(0).TestObject);
        PendingTransactionPayloadCache payloads = new(specProvider, blockFinder, 12);

        ulong includedGas = TransactionForRpc.FromTransaction(tx, new TransactionForRpcContext(
            specProvider.ChainId, TestItem.KeccakB, 1, 0, 12, 1, specProvider.GetSpec(1, 12))).Gas!.Value;
        string json = Encoding.UTF8.GetString(payloads.Get(tx).Utf8Json);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(includedGas, Is.Not.EqualTo(tx.GasLimit), "the included gas must differ from the summed limits for the check to tell them apart");
            Assert.That(json, Does.Contain($"\"gas\":\"0x{includedGas:x}\""), "a pending frame tx reports the gas the next block, which enables EIP-8141, will include it with");
        }
    }

    [Test]
    public void Get_ForAnotherTransaction_SerializesItsOwnPayload()
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance, Substitute.For<IBlockFinder>(), 12);
        payloads.AddSubscriber();
        Transaction first = Build.A.Transaction.WithNonce(0).SignedAndResolved().TestObject;
        Transaction second = Build.A.Transaction.WithNonce(1).SignedAndResolved().TestObject;

        SerializedJson firstPayload = payloads.Get(first);

        Assert.That(payloads.Get(second).Utf8Json, Is.Not.EqualTo(firstPayload.Utf8Json));
    }

    [Test]
    public void RemoveSubscriber_ForTheLastSubscriber_DropsTheKeptTransactions()
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance, Substitute.For<IBlockFinder>(), 12);
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

    [Test]
    public void RemoveSubscriber_ForTheLastSubscriber_DropsAStoreThatReadTheTableBefore()
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance, Substitute.For<IBlockFinder>(), 12);
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;
        payloads.AddSubscriber();
        AssociativeCache<ValueHash256, byte[]> readBeforeUnsubscribe = payloads.Generation;
        byte[] late = payloads.Get(transaction).Utf8Json;
        payloads.RemoveSubscriber();

        readBeforeUnsubscribe.Set(transaction.Hash!.ValueHash256, late);
        payloads.AddSubscriber();

        Assert.That(payloads.Get(transaction).Utf8Json, Is.Not.SameAs(late), "a send loop storing after the last subscriber left must not refill the table");
    }

    [Test]
    public void Get_WithoutSubscribers_DoesNotKeepThePayload()
    {
        PendingTransactionPayloadCache payloads = new(MainnetSpecProvider.Instance, Substitute.For<IBlockFinder>(), 12);
        Transaction transaction = Build.A.Transaction.SignedAndResolved().TestObject;

        SerializedJson first = payloads.Get(transaction);

        Assert.That(payloads.Get(transaction).Utf8Json, Is.Not.SameAs(first.Utf8Json), "a queue draining after its last subscriber left must not refill the table");
    }
}
