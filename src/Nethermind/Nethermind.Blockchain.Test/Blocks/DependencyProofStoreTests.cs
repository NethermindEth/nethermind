// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Blocks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test.Blocks;

[Parallelizable(ParallelScope.All)]
public class DependencyProofStoreTests
{
    [Test]
    public async Task Block_store_keeps_the_proof_apart_from_the_stored_body([Values] bool deferred, [Values(0, 320 * 1024)] int proofBytes)
    {
        await using DeferredBlockDataWriter writer = Writer(deferred);
        using IContainer container = CreateContainer(writer);
        IBlockStore blockStore = container.Resolve<IBlockStore>();
        IDependencyProofStore proofStore = container.Resolve<IDependencyProofStore>();
        Block block = BlockWithProof(1, proofBytes);
        byte[] proof = block.StarkProof!;

        blockStore.InsertDeferred(block);
        blockStore.Cache(block);
        writer.Pump();

        Block stored = blockStore.Get(block.Number, block.Hash!, shouldCache: false)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.StarkProof, Is.SameAs(proof), "the caller's block keeps its proof");
            Assert.That(stored.StarkProof, Is.Null, "the block read back carries no proof");
            Assert.That(blockStore.GetRlp(block.Number, block.Hash!), Is.EqualTo(Rlp.Encode(block).Bytes), "the proof is not part of the block encoding");
            Assert.That(proofStore.Get(block.Number, block.Hash!), Is.EqualTo(proof));
            Assert.That(proofStore.WithProof(stored).StarkProof, Is.EqualTo(proof));
            Assert.That(stored.StarkProof, Is.Null, "joining the proof leaves the stored instance without it");
        }
    }

    [Test]
    public async Task Deleting_a_body_deletes_its_proof([Values] bool deferred, [Values] bool range)
    {
        await using DeferredBlockDataWriter writer = Writer(deferred);
        using IContainer container = CreateContainer(writer);
        IBlockStore blockStore = container.Resolve<IBlockStore>();
        IDependencyProofStore proofStore = container.Resolve<IDependencyProofStore>();
        Block block = BlockWithProof(5, 64);
        Block kept = BlockWithProof(6, 64);
        blockStore.InsertDeferred(block);
        blockStore.InsertDeferred(kept);
        writer.Pump();

        if (range) blockStore.DeleteRanges([(1, 6)]);
        else blockStore.Delete(block.Number, block.Hash!);
        writer.Pump();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockStore.Get(block.Number, block.Hash!), Is.Null);
            Assert.That(proofStore.Get(block.Number, block.Hash!), Is.Null);
            Assert.That(proofStore.Get(kept.Number, kept.Hash!), Is.EqualTo(kept.StarkProof));
        }
    }

    // The proof is in neither header nor body, so a block's size is that of its encoding without it, joined or not.
    [Test]
    public async Task Joined_size_is_that_of_the_block_encoding([Values(0, 320 * 1024)] int proofBytes, [Values] bool deferred, [Values] bool cached)
    {
        await using DeferredBlockDataWriter writer = Writer(deferred);
        using IContainer container = CreateContainer(writer);
        IBlockStore blockStore = container.Resolve<IBlockStore>();
        IDependencyProofStore proofStore = container.Resolve<IDependencyProofStore>();
        Block block = BlockWithProof(1, proofBytes);
        int size = Rlp.Encode(block).Bytes.Length;
        blockStore.InsertDeferred(block);
        // A cached or pending block is a copy without a stored size, unlike one decoded from the database.
        if (cached) blockStore.Cache(block);
        Block stored = blockStore.Get(block.Number, block.Hash!, shouldCache: false)!;
        Block joined = proofStore.WithProof(stored);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(joined.StarkProof, Is.EqualTo(block.StarkProof));
            Assert.That(RpcSize(joined), Is.EqualTo(size));
            Assert.That(RpcSize(stored), Is.EqualTo(size));
        }
    }

    private static DeferredBlockDataWriter Writer(bool deferred) =>
        deferred ? DeferredWriteTestHelpers.ManualWriter() : DeferredWriteTestHelpers.DisabledWriter();

    private static IContainer CreateContainer(DeferredBlockDataWriter writer)
    {
        ContainerBuilder builder = new();
        builder.RegisterModule(new TestNethermindModule());
        builder.RegisterInstance(writer).As<IDeferredBlockDataWriter>().ExternallyOwned();
        return builder.Build();
    }

    private static Block BlockWithProof(ulong number, int proofBytes)
    {
        Block block = Build.A.Block.WithNumber(number).WithWithdrawals(0).TestObject;
        block.StarkProof = new byte[proofBytes];
        block.Header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(block.StarkProof), Keccak.Compute("deps"));
        block.Header.Hash = block.Header.CalculateHash();
        return block;
    }

    /// <summary>The size <c>BlockForRpc</c> reports.</summary>
    private static int RpcSize(Block block) => block.EncodedSize ?? new BlockDecoder().GetLength(block, RlpBehaviors.None);
}
