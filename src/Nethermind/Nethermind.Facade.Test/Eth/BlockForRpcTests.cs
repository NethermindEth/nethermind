// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Facade.Eth;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Facade.Test.Eth;

[Parallelizable(ParallelScope.All)]
public class BlockForRpcTests
{
    [Test]
    public void Size_of_decoded_block_matches_encoded_length()
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(
                Build.A.Transaction.WithNonce(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject,
                Build.A.Transaction.WithNonce(1).SignedAndResolved(TestItem.PrivateKeyA).TestObject)
            .TestObject;
        BlockDecoder decoder = new();
        Block decoded = decoder.Decode(decoder.Encode(block).Bytes)!;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.EncodedSize, Is.Not.Null);
            Assert.That(new BlockForRpc(decoded, false, MainnetSpecProvider.Instance).Size, Is.EqualTo(decoder.GetLength(block, RlpBehaviors.None)));
            Assert.That(new BlockForRpc(block, false, MainnetSpecProvider.Instance).Size, Is.EqualTo(decoded.EncodedSize));
        }
    }

    [Test]
    public void Size_computed_for_a_block_without_a_recorded_size_is_kept_on_the_block()
    {
        Block block = Build.A.Block.WithNumber(1)
            .WithTransactions(Build.A.Transaction.SignedAndResolved(TestItem.PrivateKeyA).TestObject)
            .TestObject;
        int expected = new BlockDecoder().GetLength(block, RlpBehaviors.None);
        Assert.That(block.EncodedSize, Is.Null, "precondition: a block that was not decoded has no recorded size");

        BlockForRpc rpc = new(block, false, MainnetSpecProvider.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rpc.Size, Is.EqualTo(expected));
            Assert.That(block.EncodedSize, Is.EqualTo(expected), "the next response reuses the length instead of encoding again");
        }
    }

    [Test]
    public void Size_uses_the_encoded_size_recorded_at_decode_instead_of_encoding_again()
    {
        const int recordedSize = 12_345;
        Block block = Build.A.Block.WithNumber(1).WithEncodedSize(recordedSize).TestObject;

        Assert.That(new BlockForRpc(block, false, MainnetSpecProvider.Instance).Size, Is.EqualTo(recordedSize));
    }

    [Test]
    public void Total_difficulty_is_emitted_for_pre_merge_blocks_and_chains_that_never_merge(
        [Values] bool hasTerminalTotalDifficulty, [Values] bool hasMergeBlock, [Values] bool preMergeBlock)
    {
        TestSpecProvider specProvider = new(Frontier.Instance) { TerminalTotalDifficulty = hasTerminalTotalDifficulty ? 100 : null };
        if (hasMergeBlock)
        {
            specProvider.UpdateMergeTransitionInfo(10);
        }

        Block block = Build.A.Block.WithNumber(1).WithDifficulty(preMergeBlock ? 17UL : 0UL).WithTotalDifficulty(17L).TestObject;
        bool chainNeverMerges = !hasTerminalTotalDifficulty && !hasMergeBlock;

        Assert.That(new BlockForRpc(block, false, specProvider).TotalDifficulty,
            Is.EqualTo(preMergeBlock || chainNeverMerges ? (UInt256?)17 : null));
    }
}
