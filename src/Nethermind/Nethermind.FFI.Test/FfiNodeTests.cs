// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
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
