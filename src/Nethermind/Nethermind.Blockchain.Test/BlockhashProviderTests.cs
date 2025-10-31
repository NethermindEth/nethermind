// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockhashProviderTests
{
    private static (IWorldState, Hash256) CreateWorldState()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using var _ = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
        worldState.Commit(Frontier.Instance);
        worldState.CommitTree(0);
        return (worldState, worldState.StateRoot);
    }

    private static BlockhashProvider CreateBlockHashProvider(IBlockFinder tree, IReleaseSpec spec)
    {
        (IWorldState worldState, Hash256 _) = CreateWorldState();
        BlockhashProvider provider = new(tree, new TestSpecProvider(spec), worldState, LimboLogs.Instance);
        return provider;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_get_parent_only_headers()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;

        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
        Block current = Build.A.Block.WithParent(head!).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1);
        Assert.That(result, Is.EqualTo(head?.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_up_to_256_before_with_headers_only()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 256);
        Assert.That(result, Is.EqualTo(tree.FindHeader(256, BlockTreeLookupOptions.None)!.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_correctly_on_disconnected_main_chain()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;
        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);

        BlockHeader notCanonParent = tree.FindHeader(chainLength - 4, BlockTreeLookupOptions.None)!;
        Block expected = tree.FindBlock(chainLength - 3, BlockTreeLookupOptions.None)!;

        Block headParent = tree.FindBlock(chainLength - 2, BlockTreeLookupOptions.None)!;
        Block head = tree.FindBlock(chainLength - 1, BlockTreeLookupOptions.None)!;

        Block branch = Build.A.Block.WithParent(notCanonParent).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        tree.Insert(branch, BlockTreeInsertBlockOptions.SaveHeader).Should().Be(AddBlockResult.Added);
        tree.UpdateMainChain(branch); // Update branch

        tree.UpdateMainChain([expected, headParent, head], true); // Update back to original again, but skipping the branch block.

        Block current = Build.A.Block.WithParent(head).TestObject; // At chainLength

        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 3);
        Assert.That(result, Is.EqualTo(expected.Header.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_up_to_256_before_with_headers_only_and_competing_branches()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength)
            .OfChainLength(out Block _, chainLength, 1).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        Block current = Build.A.Block.WithParent(headBlock).TestObject;
        long lookupNumber = chainLength - 256;
        Hash256? result = provider.GetBlockhash(current.Header, lookupNumber);
        Assert.That(result, Is.Not.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_up_to_256_before_soon_after_fast_sync()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength)
            .OfChainLength(out Block _, chainLength, 1).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        Block current = Build.A.Block.WithParent(headBlock).TestObject;
        tree.SuggestBlock(current);
        tree.UpdateMainChain(current);
        long lookupNumber = chainLength - 256;
        Hash256? result = provider.GetBlockhash(current.Header, lookupNumber);
        Assert.That(result, Is.Not.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_up_to_256_before_some_blocks_after_fast_sync()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength)
            .OfChainLength(out Block _, chainLength, 1).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);

        Block current = Build.A.Block.WithParent(headBlock).TestObject;
        for (int i = 0; i < 6; i++)
        {
            tree.SuggestBlock(current);
            tree.UpdateMainChain(current);
            current = Build.A.Block.WithParent(current).TestObject;
        }

        long lookupNumber = current.Number - 256;
        Hash256? result = provider.GetBlockhash(current.Header, lookupNumber);
        Assert.That(result, Is.Not.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_handle_non_main_chain_in_fast_sync()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(out Block headBlock, chainLength)
            .OfChainLength(out Block _, chainLength, 1).TestObject;
        Block current = Build.A.Block.WithParent(headBlock).TestObject;
        for (int i = 0; i < 6; i++)
        {
            tree.SuggestBlock(current);
            tree.UpdateMainChain(current);
            current = Build.A.Block.WithParent(current).TestObject;
        }

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);

        Hash256? result = provider.GetBlockhash(current.Header, 509);
        Assert.That(result, Is.Not.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_get_parent_hash()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;

        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1);
        Assert.That(result, Is.EqualTo(head.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_ask_for_self()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength);
        Assert.That(result, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Cannot_ask_about_future()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength + 1);
        Assert.That(result, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_up_to_256_before()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 256);
        Assert.That(result, Is.EqualTo(tree.FindHeader(256, BlockTreeLookupOptions.None)!.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void No_lookup_more_than_256_before()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 257);
        Assert.That(result, Is.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UInt_256_overflow()
    {
        const int chainLength = 128;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfChainLength(chainLength).TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(tree, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, 127);
        Assert.That(result, Is.EqualTo(head.Hash));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(1)]
    [TestCase(512)]
    [TestCase(8192)]
    [TestCase(8193)]
    public void Eip2935_enabled_Eip7709_disabled_and_then_get_hash(int chainLength)
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

        BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
        // number = chainLength

        (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();

        Block current = Build.A.Block.WithParent(head!).WithStateRoot(stateRoot).TestObject;
        tree.SuggestHeader(current.Header);

        var specProvider = new CustomSpecProvider(
            (new ForkActivation(0, genesis.Timestamp), Frontier.Instance),
            (new ForkActivation(0, current.Timestamp), Prague.Instance));
        BlockhashProvider provider = new(tree, specProvider, worldState, LimboLogs.Instance);
        BlockhashStore store = new(specProvider, worldState);

        using var _ = worldState.BeginScope(current.Header);

        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1);
        Assert.That(result, Is.EqualTo(head?.Hash));
        AssertGenesisHash(Prague.Instance, provider, current.Header, genesis.Hash);

        head = current.Header;
        // number = chainLength + 1
        current = Build.A.Block.WithParent(head!).TestObject;
        tree.SuggestHeader(current.Header);

        store.ApplyBlockhashStateChanges(current.Header);
        result = provider.GetBlockhash(current.Header, chainLength);
        Assert.That(result, Is.EqualTo(head?.Hash));

        AssertGenesisHash(Prague.Instance, provider, current.Header, genesis.Hash);
    }

    private static void AssertGenesisHash(IReleaseSpec spec, BlockhashProvider provider, BlockHeader currentHeader,
        Hash256? genesisHash)
    {
        Hash256? result = provider.GetBlockhash(currentHeader, 0);
        if ((spec.IsEip7709Enabled && currentHeader.Number > Eip2935Constants.RingBufferSize) || currentHeader.Number > 256)
            Assert.That(result, Is.Null);
        else
            Assert.That(result, Is.EqualTo(genesisHash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Eip2935_poc_trimmed_hashes()
    {
        var chainLength = 42;
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

        BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
        // number = chainLength

        (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();
        Block current = Build.A.Block.WithParent(head!).WithStateRoot(stateRoot).TestObject;
        tree.SuggestHeader(current.Header);

        var specProvider = new CustomSpecProvider(
            (new ForkActivation(0, genesis.Timestamp), Frontier.Instance),
            (new ForkActivation(0, current.Timestamp), Prague.Instance));
        BlockhashStore store = new(specProvider, worldState);

        using var _ = worldState.BeginScope(current.Header);

        // 1. Set some code to pass IsContract check
        byte[] code = [1, 2, 3];
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, Prague.Instance);

        current.Header.ParentHash = new Hash256("0x0011111111111111111111111111111111111111111111111111111111111111");
        // 2. Store parent hash with leading zeros
        store.ApplyBlockhashStateChanges(current.Header);
        // 3. Try to retrieve the parent hash from the state
        var result = store.GetBlockHashFromState(current.Header, current.Header.Number - 1);
        Assert.That(result, Is.EqualTo(current.Header.ParentHash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockhashStore_uses_custom_ring_buffer_size()
    {
        const int customRingBufferSize = 100;
        const int chainLength = 150;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;
        BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);

        (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();
        Block current = Build.A.Block.WithParent(head!).WithStateRoot(stateRoot).TestObject;
        tree.SuggestHeader(current.Header);

        // Custom spec with non-standard ring buffer size
        ReleaseSpec customSpec = new()
        {
            IsEip2935Enabled = true,
            IsEip7709Enabled = true,
            Eip2935RingBufferSize = customRingBufferSize
        };

        var specProvider = new CustomSpecProvider(
            (new ForkActivation(0, genesis.Timestamp), customSpec));
        BlockhashStore store = new(specProvider, worldState);

        using IDisposable _ = worldState.BeginScope(current.Header);

        // Insert code (account already created by CreateWorldState)
        byte[] code = [1, 2, 3];
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, customSpec);

        // Process current block (150) - stores block 149's hash
        store.ApplyBlockhashStateChanges(current.Header);

        // Simulate processing blocks 51-149 to fill the ring buffer
        // At block 150 with buffer size 100, we need hashes for blocks 50-149
        // Block 150 stores block 149's hash (already done above)
        // Now process blocks 51-149 from the tree to store blocks 50-148
        for (long blockNum = chainLength - customRingBufferSize + 1; blockNum < chainLength; blockNum++)
        {
            BlockHeader? header = tree.FindHeader(blockNum, BlockTreeLookupOptions.None);
            Assert.That(header, Is.Not.Null, $"Block {blockNum} should exist in tree");

            store.ApplyBlockhashStateChanges(header!);
        }

        // Now verify all blocks behave correctly with custom ring buffer size
        // At block 150 with buffer size 100, only blocks [50, 149] should be retrievable
        for (long blockNum = 1; blockNum < chainLength - customRingBufferSize; blockNum++)
        {
            Hash256? result = store.GetBlockHashFromState(current.Header, blockNum, customSpec);

            Assert.That(result, Is.Null,
                $"Block {blockNum} should be outside custom ring buffer of size {customRingBufferSize} (proves custom size is used, not default 8191)");
        }

        for (long blockNum = chainLength - customRingBufferSize; blockNum < chainLength; blockNum++)
        {
            BlockHeader? expectedHeader = tree.FindHeader(blockNum, BlockTreeLookupOptions.None);
            Hash256? result = store.GetBlockHashFromState(current.Header, blockNum, customSpec);

            Assert.That(result, Is.EqualTo(expectedHeader!.Hash),
                $"Block {blockNum} should be retrievable within custom ring buffer of size {customRingBufferSize}");
        }
    }
}
