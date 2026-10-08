// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.FFI.Test;

public class FfiNodeTests
{
    public enum Mutation
    {
        None,
        WrongStateRoot,
        WrongGasLimit,
        UnknownParent,
        Garbage,
    }

    [TestCase(Mutation.None, FfiStatus.Ok)]
    [TestCase(Mutation.WrongStateRoot, FfiStatus.InvalidBlock)]
    [TestCase(Mutation.WrongGasLimit, FfiStatus.InvalidBlock)]
    [TestCase(Mutation.UnknownParent, FfiStatus.ParentNotFound)]
    [TestCase(Mutation.Garbage, FfiStatus.DecodeError)]
    public async Task ExecuteBlock_validates_and_executes_without_persisting(Mutation mutation, FfiStatus expected)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create();
        await chain.BuildSomeBlocks(2);
        using ILifetimeScope scope = FfiNode.CreateScope(chain.Container);
        FfiNode node = scope.Resolve<FfiNode>();
        Block head = chain.BlockTree.Head!;

        byte[] blockRlp = mutation is Mutation.Garbage ? Bytes.FromHexString("0xdeadbeef") : Rlp.Encode(Mutate(head, mutation)).Bytes;
        BlockExecutionResult result = node.ExecuteBlock(blockRlp);

        Assert.That(result.Status, Is.EqualTo(expected), result.Error);
        Assert.That(chain.BlockTree.Head, Is.SameAs(head));
        Assert.That(node.Head, Is.SameAs(head.Header));
        if (expected is not FfiStatus.Ok) return;

        RlpReader reader = new(result.ReceiptsRlp!);
        TxReceipt[] receipts = Rlp.GetDecoderOrThrow<TxReceipt>().DecodeArray(ref reader)!;
        Assert.That(result.Header!.Hash, Is.EqualTo(head.Hash));
        Assert.That(result.Header.StateRoot, Is.EqualTo(head.StateRoot));
        Assert.That(result.Header.GasUsed, Is.EqualTo(head.GasUsed));
        Assert.That(receipts, Has.Length.EqualTo(head.Transactions.Length));
        Assert.That(ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, chain.SpecProvider.GetSpec(head.Header), head.ReceiptsRoot),
            Is.EqualTo(head.ReceiptsRoot));
    }

    private static readonly ConcurrentQueue<(FfiTxEvent Event, Hash256 Hash, byte[] Tx)> TxEvents = new();

    [Test]
    public async Task Tx_callback_reports_pool_events_until_cleared()
    {
        TxEvents.Clear();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(static builder =>
            builder.AddSingleton<ITxPoolConfig>(new TxPoolConfig { Size = 1 }));
        using ILifetimeScope scope = FfiNode.CreateScope(chain.Container);
        FfiNode node = scope.Resolve<FfiNode>();
        unsafe
        {
            node.SetTxCallback(&RecordTxEvent, 0);
        }

        // The pool holds one tx, so the better-paying one evicts the first before being included in the block.
        Transaction evicted = Transfer(chain, TestItem.PrivateKeyA, gasPrice: 1);
        Transaction included = Transfer(chain, TestItem.PrivateKeyB, gasPrice: 10);
        Assert.That(chain.TxPool.SubmitTx(evicted, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        await chain.AddBlock(included);

        (FfiTxEvent, Hash256)[] expected =
        [
            (FfiTxEvent.Pending, evicted.Hash!),
            (FfiTxEvent.Evicted, evicted.Hash!),
            (FfiTxEvent.Pending, included.Hash!),
            (FfiTxEvent.Removed, included.Hash!),
        ];
        Assert.That(() => TxEvents.Select(static e => (e.Event, e.Hash)), Is.EqualTo(expected).After(5000, 50));
        Assert.That(TxEvents.First().Tx, Is.EqualTo(TxDecoder.Instance.Encode(evicted, RlpBehaviors.SkipTypedWrapping).Bytes));

        unsafe
        {
            node.SetTxCallback(null, 0);
        }

        Assert.That(chain.TxPool.SubmitTx(Transfer(chain, TestItem.PrivateKeyA, gasPrice: 20), TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        Assert.That(TxEvents, Has.Count.EqualTo(expected.Length));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void RecordTxEvent(nint userData, int txEvent, byte* hash, byte* tx, nuint txLength) =>
        TxEvents.Enqueue(((FfiTxEvent)txEvent, new Hash256(new ReadOnlySpan<byte>(hash, Hash256.Size)), new ReadOnlySpan<byte>(tx, (int)txLength).ToArray()));

    private static Transaction Transfer(BasicTestBlockchain chain, PrivateKey sender, ulong gasPrice) => Build.A.Transaction
        .WithTo(TestItem.AddressD)
        .WithGasPrice(gasPrice)
        .WithGasLimit(GasCostOf.Transaction)
        .SignedAndResolved(sender, chain.SpecProvider.GetSpec(chain.BlockTree.Head!.Header).IsEip155Enabled)
        .TestObject;

    private static Block Mutate(Block block, Mutation mutation)
    {
        BlockHeader header = block.Header.Clone();
        switch (mutation)
        {
            case Mutation.WrongStateRoot:
                header.StateRoot = TestItem.KeccakA;
                break;
            case Mutation.WrongGasLimit:
                header.GasLimit *= 2;
                break;
            case Mutation.UnknownParent:
                header.ParentHash = TestItem.KeccakB;
                break;
        }

        header.Hash = header.CalculateHash();
        return new Block(header, block.Body);
    }
}
