// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Headers;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Core.Eip2930;
using Nethermind.Int256;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Blockchain.Test;

[Parallelizable(ParallelScope.All)]
public class BlockhashProviderTests
{
    private static (IWorldState, Hash256) CreateWorldState()
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
        worldState.Commit(Frontier.Instance);
        worldState.CommitTree(0);
        return (worldState, worldState.StateRoot);
    }

    private static IWorldState CreateWorldStateWithHistoryContract(IReleaseSpec spec)
    {
        IWorldState worldState = TestWorldStateFactory.CreateForTest();
        using IDisposable _ = worldState.BeginScope(IWorldState.PreGenesis);
        worldState.CreateAccount(Eip2935Constants.BlockHashHistoryAddress, 0, 1);
        byte[] code = [1, 2, 3];
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, spec);
        worldState.Commit(spec);
        worldState.CommitTree(0);
        return worldState;
    }


    [TestCase(true, false, true, true, TestName = "GetAccessList_WithDeployedContract_CoversTheParentHashSlot")]
    [TestCase(false, false, true, false, TestName = "GetAccessList_BeforeEip2935_IsNull")]
    [TestCase(true, true, true, false, TestName = "GetAccessList_ForGenesis_IsNull")]
    [TestCase(true, false, false, false, TestName = "GetAccessList_WithoutDeployedContract_IsNull")]
    public void GetAccessList_AtGivenForkAndState_HintsExactlyTheParentHashSlot(
        bool eip2935Enabled, bool isGenesis, bool contractDeployed, bool expectList)
    {
        IReleaseSpec spec = eip2935Enabled ? Prague.Instance : Cancun.Instance;
        (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();
        Block parent = Build.A.Block.WithNumber(41).TestObject;
        Block current = isGenesis
            ? Build.A.Block.Genesis.WithStateRoot(stateRoot).TestObject
            : Build.A.Block.WithParent(parent).WithStateRoot(stateRoot).TestObject;

        using IDisposable scope = worldState.BeginScope(current.Header);
        if (contractDeployed)
        {
            byte[] code = [1, 2, 3];
            worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, spec);
        }

        AccessList? accessList = new BlockhashStore(worldState).GetAccessList(current, spec);

        if (!expectList)
        {
            Assert.That(accessList, Is.Null);
            return;
        }

        UInt256 expectedSlot = new((current.Number - 1) % spec.Eip2935RingBufferSize);
        Assert.That(accessList, Is.Not.Null);
        foreach ((Address address, AccessList.StorageKeysEnumerable storageKeys) in accessList!)
        {
            Assert.That(address, Is.EqualTo(Eip2935Constants.BlockHashHistoryAddress));
            foreach (UInt256 storageKey in storageKeys)
            {
                Assert.That(storageKey, Is.EqualTo(expectedSlot), "the hint must cover exactly the ring-buffer slot the block writes");
            }
        }
    }

    private static BlockhashProvider CreateBlockHashProvider(IHeaderFinder headerFinder, IReleaseSpec spec)
    {
        (IWorldState worldState, Hash256 _) = CreateWorldState();
        BlockhashProvider provider = new(new BlockhashCache(headerFinder, LimboLogs.Instance), worldState, LimboLogs.Instance);
        return provider;
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_get_parent_only_headers()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;

        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);
        BlockHeader? head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None);
        Block current = Build.A.Block.WithParent(head!).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1, Frontier.Instance);
        Assert.That(result, Is.EqualTo(head?.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_up_to_256_before_with_headers_only()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 256, Frontier.Instance);
        Assert.That(result, Is.EqualTo(tree.FindHeader(256, BlockTreeLookupOptions.None)!.Hash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_lookup_correctly_on_disconnected_main_chain()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfChainLength(chainLength);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);

        BlockHeader notCanonParent = tree.FindHeader(chainLength - 4, BlockTreeLookupOptions.None)!;
        BlockHeader expectedHeader = tree.FindHeader(chainLength - 3, BlockTreeLookupOptions.None)!;
        Block headParent = tree.FindBlock(chainLength - 2, BlockTreeLookupOptions.None)!;
        Block head = tree.FindBlock(chainLength - 1, BlockTreeLookupOptions.None)!;

        Block branch = Build.A.Block.WithParent(notCanonParent).WithTransactions(Build.A.Transaction.TestObject).TestObject;
        Assert.That(tree.Insert(branch, BlockTreeInsertBlockOptions.SaveHeader), Is.EqualTo(AddBlockResult.Added));
        tree.TryUpdateMainChain(branch.Header, true, preloadedBlocks: new[] { branch }); // Update branch

        tree.TryUpdateMainChain(head.Header, true, preloadedBlocks: new[] { headParent, head }); // Update back to original again, but skipping the branch block.

        Block current = Build.A.Block.WithParent(head).TestObject; // At chainLength

        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 3, Frontier.Instance);
        Assert.That(result, Is.EqualTo(expectedHeader.Hash));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(0, TestName = "Can_lookup_up_to_256_before_with_headers_only_and_competing_branches")]
    [TestCase(1, TestName = "Can_lookup_up_to_256_before_soon_after_fast_sync")]
    [TestCase(6, TestName = "Can_lookup_up_to_256_before_some_blocks_after_fast_sync")]
    public void Lookup_with_competing_branches_after_fast_sync(int additionalBlocks)
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfHeadersOnly
            .OfChainLength(out Block headBlock, chainLength)
            .OfChainLength(out Block _, chainLength, 1);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);

        Block current = Build.A.Block.WithParent(headBlock).TestObject;
        for (int i = 0; i < additionalBlocks; i++)
        {
            tree.SuggestBlock(current);
            tree.TryUpdateMainChain(current.Header, true, preloadedBlocks: new[] { current });
            current = Build.A.Block.WithParent(current).TestObject;
        }

        ulong lookupNumber = current.Number - 256ul;
        Hash256? result = provider.GetBlockhash(current.Header, lookupNumber, Frontier.Instance);
        Assert.That(result, Is.Not.Null);
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Can_handle_non_main_chain_in_fast_sync()
    {
        const int chainLength = 512;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfHeadersOnly
            .OfChainLength(out Block headBlock, chainLength)
            .OfChainLength(out Block _, chainLength, 1);
        BlockTree tree = blockTreeBuilder.TestObject;
        Block current = Build.A.Block.WithParent(headBlock).TestObject;
        for (int i = 0; i < 6; i++)
        {
            tree.SuggestBlock(current);
            tree.TryUpdateMainChain(current.Header, true, preloadedBlocks: new[] { current });
            current = Build.A.Block.WithParent(current).TestObject;
        }

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);

        Hash256? result = provider.GetBlockhash(current.Header, 509, Frontier.Instance);
        Assert.That(result, Is.Not.Null);
    }

    [MaxTime(Timeout.MaxTestTime)]
    [TestCase(512ul, -1, true, TestName = "Can_get_parent_hash")]
    [TestCase(512ul, 0, false, TestName = "Cannot_ask_for_self")]
    [TestCase(512ul, 1, false, TestName = "Cannot_ask_about_future")]
    [TestCase(512ul, -256, true, TestName = "Can_lookup_up_to_256_before")]
    [TestCase(512ul, -257, false, TestName = "No_lookup_more_than_256_before")]
    public void Blockhash_lookup_with_full_chain(ulong chainLength, int lookupOffset, bool expectNonNull)
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfChainLength(chainLength);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1ul, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        ulong lookupNumber = (ulong)((long)chainLength + lookupOffset);
        Hash256? result = provider.GetBlockhash(current.Header, lookupNumber, Frontier.Instance);

        if (expectNonNull)
        {
            Hash256 expected = tree.FindHeader(lookupNumber, BlockTreeLookupOptions.None)!.Hash!;
            Assert.That(result, Is.EqualTo(expected));
        }
        else
        {
            Assert.That(result, Is.Null);
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void UInt_256_overflow()
    {
        const int chainLength = 128;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfChainLength(chainLength);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockhashProvider provider = CreateBlockHashProvider(blockTreeBuilder.HeaderStore, Frontier.Instance);
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        Block current = Build.A.Block.WithParent(head).TestObject;
        Hash256? result = provider.GetBlockhash(current.Header, 127, Frontier.Instance);
        Assert.That(result, Is.EqualTo(head.Hash));
    }

    [MaxTime(Timeout.MaxTestTime)]
    [Test]
    public void Eip2935_enabled_Eip7709_disabled_and_then_get_hash([Values(1ul, 512ul, 8192ul, 8193ul)] ulong chainLength)
    {
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength);
        BlockTree tree = blockTreeBuilder.TestObject;

        BlockHeader? head = tree.FindHeader(chainLength - 1ul, BlockTreeLookupOptions.None);
        // number = chainLength

        (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();

        Block current = Build.A.Block.WithParent(head!).WithStateRoot(stateRoot).TestObject;
        tree.SuggestHeader(current.Header);

        CustomSpecProvider specProvider = new(
            (new ForkActivation(0, genesis.Timestamp), Frontier.Instance),
            (new ForkActivation(0, current.Timestamp), Prague.Instance));
        BlockhashProvider provider = new(new BlockhashCache(blockTreeBuilder.HeaderStore, LimboLogs.Instance), worldState, LimboLogs.Instance);
        BlockhashStore store = new(worldState);

        using IDisposable _ = worldState.BeginScope(current.Header);

        Hash256? result = provider.GetBlockhash(current.Header, chainLength - 1, Frontier.Instance);
        Assert.That(result, Is.EqualTo(head?.Hash));
        AssertGenesisHash(Prague.Instance, provider, current.Header, genesis.Hash);

        head = current.Header;
        // number = chainLength + 1
        current = Build.A.Block.WithParent(head!).TestObject;
        tree.SuggestHeader(current.Header);

        store.ApplyBlockhashStateChanges(current.Header, specProvider.GetSpec(current.Header));
        result = provider.GetBlockhash(current.Header, chainLength, Frontier.Instance);
        Assert.That(result, Is.EqualTo(head?.Hash));

        AssertGenesisHash(Prague.Instance, provider, current.Header, genesis.Hash);
    }

    private static void AssertGenesisHash(IReleaseSpec spec, BlockhashProvider provider, BlockHeader currentHeader, Hash256? genesisHash)
    {
        Hash256? result = provider.GetBlockhash(currentHeader, 0, spec);
        Assert.That(result, (spec.IsEip7709Enabled && currentHeader.Number > Eip2935Constants.RingBufferSize) || currentHeader.Number > 256
                ? Is.Null
                : Is.EqualTo(genesisHash));
    }

    /// <summary>Chain, state, store and provider wiring shared by the blockhash span-lookup tests.</summary>
    /// <remarks>The storage-backed path is gated on EIP-7709, which no named fork enables yet, so the spec is
    /// built explicitly rather than taken from a fork.</remarks>
    private sealed class BlockhashFixture : IDisposable
    {
        private const ulong ChainLength = 42ul;
        private readonly IDisposable _scope;

        public BlockhashFixture(bool blockHashInState = true)
        {
            Block genesis = Build.A.Block.Genesis.TestObject;
            BlockTreeBuilder builder = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(ChainLength);
            BlockTree tree = builder.TestObject;
            Tree = tree;
            Head = tree.FindHeader(ChainLength - 1ul, BlockTreeLookupOptions.None)!;

            (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();
            WorldState = worldState;
            StateRoot = stateRoot;
            Current = Build.A.Block.WithParent(Head).WithStateRoot(stateRoot).TestObject;
            tree.SuggestHeader(Current.Header);

            Spec = new ReleaseSpec
            {
                IsEip2935Enabled = true,
                IsEip7709Enabled = blockHashInState,
                Eip2935RingBufferSize = Eip2935Constants.RingBufferSize
            };

            Store = new BlockhashStore(worldState);
            Provider = new BlockhashProvider(new BlockhashCache(builder.HeaderStore, LimboLogs.Instance), worldState, LimboLogs.Instance);

            _scope = worldState.BeginScope(Current.Header);
            byte[] code = [1, 2, 3];
            worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, Prague.Instance);
        }

        public BlockTree Tree { get; }

        public BlockHeader Head { get; }
        public Block Current { get; }
        public Hash256 StateRoot { get; }
        public IWorldState WorldState { get; }
        public ReleaseSpec Spec { get; }
        public BlockhashStore Store { get; }
        public BlockhashProvider Provider { get; }

        /// <summary>Writes <paramref name="parentHash"/> into the ring slot that <paramref name="header"/> owns.</summary>
        public void StoreParentHash(BlockHeader header, Hash256 parentHash)
        {
            header.ParentHash = parentHash;
            Store.ApplyBlockhashStateChanges(header, Spec);
        }

        /// <summary>Another header at the same height, which therefore shares the ring slot.</summary>
        public BlockHeader BuildSibling() => Build.A.Block.WithParent(Head).WithStateRoot(StateRoot).TestObject.Header;

        public void Dispose() => _scope.Dispose();
    }

    /// <summary>A cached entry must never outlive the block it was resolved for.</summary>
    /// <remarks>Two headers at the same height writing different parent hashes land on the same ring slot,
    /// which is the case a per-block memo gets wrong if it keys on the block number alone.</remarks>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Eip2935_cache_does_not_serve_another_block()
    {
        using BlockhashFixture fixture = new();
        BlockHeader header = fixture.Current.Header;
        ulong number = header.Number - 1;

        Hash256 firstParent = new("0x1111111111111111111111111111111111111111111111111111111111111111");
        fixture.StoreParentHash(header, firstParent);
        // Arm the memo so the sibling is rejected by the per-entry reference check rather than by the
        // unarmed fallback: the sibling is byte-identical to this header, so it shares the armed hash.
        fixture.Provider.Prefetch(header, CancellationToken.None).GetAwaiter().GetResult();

        Assert.That(fixture.Provider.TryGetBlockhash(header, number, fixture.Spec, out ReadOnlySpan<byte> first), Is.True);
        Assert.That(first.ToArray(), Is.EqualTo(firstParent.Bytes.ToArray()), "first block");

        // A second header at the same height overwrites the same ring slot.
        Hash256 secondParent = new("0x2222222222222222222222222222222222222222222222222222222222222222");
        BlockHeader secondHeader = fixture.BuildSibling();
        fixture.StoreParentHash(secondHeader, secondParent);

        Assert.That(fixture.Provider.TryGetBlockhash(secondHeader, number, fixture.Spec, out ReadOnlySpan<byte> second), Is.True);
        Assert.That(second.ToArray(), Is.EqualTo(secondParent.Bytes.ToArray()), "a second block must not be served the first entry");
    }

    /// <summary>Repeated lookups must keep agreeing with a direct read from state.</summary>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Eip2935_cached_lookup_matches_uncached()
    {
        using BlockhashFixture fixture = new();
        BlockHeader header = fixture.Current.Header;
        fixture.Store.ApplyBlockhashStateChanges(header, fixture.Spec);
        fixture.Provider.Prefetch(header, CancellationToken.None).GetAwaiter().GetResult();

        for (int round = 0; round < 3; round++)
        {
            for (ulong number = 0; number < header.Number; number++)
            {
                Hash256? expected = fixture.Store.GetBlockHashFromState(header, number, fixture.Spec);
                bool found = fixture.Provider.TryGetBlockhash(header, number, fixture.Spec, out ReadOnlySpan<byte> actual);

                Assert.That(found, Is.EqualTo(expected is not null), $"round {round}, number {number}");
                if (expected is not null)
                {
                    Assert.That(actual.ToArray(), Is.EqualTo(expected.Bytes.ToArray()), $"round {round}, number {number}");
                }
            }
        }
    }

    /// <summary>A sweep over distinct numbers allocates at most one memo entry per number per block:
    /// the second pass over the same distinct set must be allocation-free.</summary>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Blockhash_span_lookup_over_distinct_numbers_allocates_once_per_number()
    {
        using BlockhashFixture fixture = new();
        BlockHeader header = fixture.Current.Header;
        fixture.Store.ApplyBlockhashStateChanges(header, fixture.Spec);
        fixture.Provider.Prefetch(header, CancellationToken.None).GetAwaiter().GetResult();
        for (ulong k = 1; k < 42; k++)
        {
            fixture.Store.ApplyBlockhashStateChanges(fixture.Tree.FindHeader(k, BlockTreeLookupOptions.None)!, fixture.Spec);
        }

        for (ulong n = 1; n < 42; n++)
        {
            Assert.That(fixture.Provider.TryGetBlockhash(header, n, fixture.Spec, out _), Is.True, $"number {n} should resolve");
        }

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (ulong n = 1; n < 42; n++)
        {
            fixture.Provider.TryGetBlockhash(header, n, fixture.Spec, out _);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - start, Is.Zero);
    }

    /// <summary>The memo must hit for the header the block actually executes with, which is a
    /// CloneForProcessing of the suggested header — a different instance, same number and hash.</summary>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Blockhash_memo_hits_the_processing_clone_not_only_the_armed_instance()
    {
        using BlockhashFixture fixture = new();
        BlockHeader suggested = fixture.Current.Header;
        fixture.Store.ApplyBlockhashStateChanges(suggested, fixture.Spec);
        fixture.Provider.Prefetch(suggested, CancellationToken.None).GetAwaiter().GetResult();
        ulong number = suggested.Number - 1;

        // Block processing executes with the clone, never the armed instance.
        BlockHeader processing = suggested.CloneForProcessing();
        Assert.That(ReferenceEquals(processing, suggested), Is.False, "precondition: distinct instance");

        Assert.That(fixture.Provider.TryGetBlockhash(processing, number, fixture.Spec, out _), Is.True, "warm the memo via the clone");

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            fixture.Provider.TryGetBlockhash(processing, number, fixture.Spec, out _);
        }

        Assert.That(GC.GetAllocatedBytesForCurrentThread() - start, Is.Zero, "the clone must hit the armed memo");
    }

    /// <summary>An unarmed provider (one that never prefetched) must never serve the memo — it re-reads
    /// state every call, which is what protects the pooled RPC envs that execute under state overrides.</summary>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Blockhash_unarmed_provider_reflects_a_state_rewrite()
    {
        using BlockhashFixture fixture = new();
        BlockHeader header = fixture.Current.Header;
        ulong number = header.Number - 1;

        Hash256 firstParent = new("0x1111111111111111111111111111111111111111111111111111111111111111");
        fixture.StoreParentHash(header, firstParent);
        // No Prefetch: this provider is unarmed, exactly like a pooled eth_call env.
        Assert.That(fixture.Provider.TryGetBlockhash(header, number, fixture.Spec, out ReadOnlySpan<byte> before), Is.True);
        Assert.That(before.ToArray(), Is.EqualTo(firstParent.Bytes.ToArray()));

        // Rewrite the history slot through the same world state (the shape a stateOverride produces).
        Hash256 secondParent = new("0x2222222222222222222222222222222222222222222222222222222222222222");
        fixture.StoreParentHash(header, secondParent);

        Assert.That(fixture.Provider.TryGetBlockhash(header, number, fixture.Spec, out ReadOnlySpan<byte> after), Is.True);
        Assert.That(after.ToArray(), Is.EqualTo(secondParent.Bytes.ToArray()), "an unarmed read must reflect the rewrite, not a memoized value");
    }

    /// <summary>The span overload is the BLOCKHASH path, so it must not allocate per lookup.</summary>
    /// <remarks>Goes through the production <see cref="BlockhashProvider"/> on both the block-tree path and the
    /// storage-backed one, so the block-tree case doubles as a control against regressing it. The allocating
    /// overload is measured in the same run, so the comparison fails loudly rather than passing vacuously.</remarks>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Blockhash_span_lookup_does_not_allocate([Values(true, false)] bool blockHashInState)
    {
        const int Iterations = 1000;

        using BlockhashFixture fixture = new(blockHashInState);
        BlockHeader header = fixture.Current.Header;
        fixture.Store.ApplyBlockhashStateChanges(header, fixture.Spec);
        fixture.Provider.Prefetch(header, CancellationToken.None).GetAwaiter().GetResult();
        ulong number = header.Number - 1;

        for (int i = 0; i < Iterations; i++)
        {
            fixture.Provider.TryGetBlockhash(header, number, fixture.Spec, out _);
            fixture.Provider.GetBlockhash(header, number, fixture.Spec);
        }

        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            fixture.Provider.TryGetBlockhash(header, number, fixture.Spec, out _);
        }
        long spanAllocated = GC.GetAllocatedBytesForCurrentThread() - start;

        start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            fixture.Provider.GetBlockhash(header, number, fixture.Spec);
        }
        long hashAllocated = GC.GetAllocatedBytesForCurrentThread() - start;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spanAllocated, Is.Zero, $"span={spanAllocated} hash={hashAllocated}");
            Assert.That(hashAllocated, blockHashInState ? Is.GreaterThan(Iterations * 8) : Is.Zero,
                "only the storage-backed path materialises a Hash256 per lookup");
        }
    }

    /// <summary>The span overload must left-pad exactly as the <see cref="Hash256"/> overload does.</summary>
    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Eip2935_trimmed_hashes_pad_identically(
        [Values("0x0011111111111111111111111111111111111111111111111111111111111111",
                "0x0000000000000000000000000000000000000000000000000000000000000011",
                "0xff11111111111111111111111111111111111111111111111111111111111111",
                "0x0000000000000000000000000000000000000000000000000000000000000000")] string parentHash)
    {
        using BlockhashFixture fixture = new();
        BlockHeader header = fixture.Current.Header;
        fixture.StoreParentHash(header, new Hash256(parentHash));
        ulong number = header.Number - 1;

        Hash256? expected = fixture.Store.GetBlockHashFromState(header, number, fixture.Spec);

        Span<byte> actual = stackalloc byte[Hash256.Size];
        bool found = fixture.Store.TryGetBlockHashFromState(header, number, fixture.Spec, actual);

        Assert.That(found, Is.EqualTo(expected is not null));
        if (expected is not null)
        {
            Assert.That(actual.ToArray(), Is.EqualTo(expected.Bytes.ToArray()));
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Eip2935_poc_trimmed_hashes()
    {
        ulong chainLength = 42ul;
        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTree tree = Build.A.BlockTree(genesis).OfHeadersOnly.OfChainLength(chainLength).TestObject;

        BlockHeader? head = tree.FindHeader(chainLength - 1ul, BlockTreeLookupOptions.None);
        // number = chainLength

        (IWorldState worldState, Hash256 stateRoot) = CreateWorldState();
        Block current = Build.A.Block.WithParent(head!).WithStateRoot(stateRoot).TestObject;
        tree.SuggestHeader(current.Header);

        ISpecProvider specProvider = new CustomSpecProvider(
            (new ForkActivation(0, genesis.Timestamp), Frontier.Instance),
            (new ForkActivation(0, current.Timestamp), Prague.Instance));
        BlockhashStore store = new(worldState);

        using IDisposable _ = worldState.BeginScope(current.Header);

        // 1. Set some code to pass IsContract check
        byte[] code = [1, 2, 3];
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, Prague.Instance);

        current.Header.ParentHash = new Hash256("0x0011111111111111111111111111111111111111111111111111111111111111");
        // 2. Store parent hash with leading zeros
        store.ApplyBlockhashStateChanges(current.Header, specProvider.GetSpec(current.Header));
        // 3. Try to retrieve the parent hash from the state
        Hash256? result = store.GetBlockHashFromState(current.Header, current.Header.Number - 1, specProvider.GetSpec(current.Header));
        Assert.That(result, Is.EqualTo(current.Header.ParentHash));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void BlockAccessListManager_blockhash_state_changes_match_BlockhashStore()
    {
        IReleaseSpec spec = Amsterdam.Instance;
        IWorldState legacyWorldState = CreateWorldStateWithHistoryContract(spec);
        IWorldState balWorldState = CreateWorldStateWithHistoryContract(spec);
        Block parent = Build.A.Block.WithNumber(41).TestObject;
        Block current = Build.A.Block.WithParent(parent).TestObject;
        UInt256 parentBlockIndex = new((current.Number - 1) % spec.Eip2935RingBufferSize);
        StorageCell storageCell = new(Eip2935Constants.BlockHashHistoryAddress, parentBlockIndex);

        using IDisposable legacyScope = legacyWorldState.BeginScope(current.Header);
        new BlockhashStore(legacyWorldState).ApplyBlockhashStateChanges(current.Header, spec);
        byte[] expectedStoredHash = legacyWorldState.Get(storageCell).ToArray();

        using IDisposable balScope = balWorldState.BeginScope(current.Header);
        TestSingleReleaseSpecProvider specProvider = new(spec);
        BlockAccessListManager balManager = new(
            balWorldState,
            LimboLogs.Instance,
            new BlocksConfig { ParallelExecution = false },
            new WithdrawalProcessorFactory(LimboLogs.Instance),
            new BalTxProcessorFactory(Substitute.For<IBlockhashProvider>(), specProvider, LimboLogs.Instance));
        balManager.PrepareForProcessing(current, spec, ProcessingOptions.None);
        balManager.SetBlockExecutionContext(new BlockExecutionContext(current.Header, spec));
        balManager.Setup(current);

        balManager.ApplyBlockhashStateChanges(current.Header, spec);
        balManager.NextTransaction();

        Assert.That(balWorldState.Get(storageCell).ToArray(), Is.EqualTo(expectedStoredHash));
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

        CustomSpecProvider specProvider = new((new ForkActivation(0, genesis.Timestamp), customSpec));
        BlockhashStore store = new(worldState);

        using IDisposable _ = worldState.BeginScope(current.Header);

        // Insert code (account already created by CreateWorldState)
        byte[] code = [1, 2, 3];
        worldState.InsertCode(Eip2935Constants.BlockHashHistoryAddress, ValueKeccak.Compute(code), code, customSpec);

        // Process current block (150) - stores block 149's hash
        store.ApplyBlockhashStateChanges(current.Header, specProvider.GetSpec(current.Header));

        // Simulate processing blocks 51-149 to fill the ring buffer
        // At block 150 with buffer size 100, we need hashes for blocks 50-149
        // Block 150 stores block 149's hash (already done above)
        // Now process blocks 51-149 from the tree to store blocks 50-148
        for (ulong blockNum = chainLength - customRingBufferSize + 1; blockNum < chainLength; blockNum++)
        {
            BlockHeader? header = tree.FindHeader(blockNum, BlockTreeLookupOptions.None);
            Assert.That(header, Is.Not.Null, $"Block {blockNum} should exist in tree");

            store.ApplyBlockhashStateChanges(header!, specProvider.GetSpec(header));
        }

        // Now verify all blocks behave correctly with custom ring buffer size
        // At block 150 with buffer size 100, only blocks [50, 149] should be retrievable
        for (ulong blockNum = 1ul; blockNum < (chainLength - customRingBufferSize); blockNum++)
        {
            Hash256? result = store.GetBlockHashFromState(current.Header, blockNum, customSpec);

            Assert.That(result, Is.Null,
                $"Block {blockNum} should be outside custom ring buffer of size {customRingBufferSize} (proves custom size is used, not default 8191)");
        }

        for (ulong blockNum = chainLength - customRingBufferSize; blockNum < chainLength; blockNum++)
        {
            BlockHeader? expectedHeader = tree.FindHeader(blockNum, BlockTreeLookupOptions.None);
            Hash256? result = store.GetBlockHashFromState(current.Header, blockNum, customSpec);

            Assert.That(result, Is.EqualTo(expectedHeader!.Hash),
                $"Block {blockNum} should be retrievable within custom ring buffer of size {customRingBufferSize}");
        }
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    public void Throws_when_in_window_hash_cannot_be_resolved()
    {
        IHeaderFinder headerFinder = Substitute.For<IHeaderFinder>();
        BlockhashProvider provider = CreateBlockHashProvider(headerFinder, Frontier.Instance);
        BlockHeader current = Build.A.BlockHeader.WithNumber(300).WithParentHash(TestItem.KeccakA).TestObject;

        Assert.Throws<InvalidDataException>(() => provider.GetBlockhash(current, 100, Frontier.Instance));
    }

    [Test, MaxTime(Timeout.MaxTestTime)]
    [NonParallelizable]
    public async Task Prefetches_come_in_wrong_order()
    {
        const int chainLength = 261;

        Block genesis = Build.A.Block.Genesis.TestObject;
        BlockTreeBuilder blockTreeBuilder = Build.A.BlockTree(genesis)
            .OfHeadersOnly
            .OfChainLength(chainLength);
        SlowHeaderStore slowHeaderStore = new(blockTreeBuilder.HeaderStore) { SlowBlockNumber = 2 };
        BlockTree tree = blockTreeBuilder.TestObject;
        BlockHeader head = tree.FindHeader(chainLength - 1, BlockTreeLookupOptions.None)!;
        ulong expectedBlockNumber = head.Number - 2;
        BlockHeader expected = tree.FindHeader(expectedBlockNumber, BlockTreeLookupOptions.None)!;
        BlockHeader previousHead = tree.FindHeader(chainLength - 4, BlockTreeLookupOptions.None)!;
        BlockhashProvider provider = CreateBlockHashProvider(slowHeaderStore, Frontier.Instance);
        CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(5));
        Task previousHeadTask = provider.Prefetch(previousHead, cts.Token);
        Task headTask = provider.Prefetch(head, CancellationToken.None);
        await Task.WhenAll(previousHeadTask, headTask);

        Hash256? result = provider.GetBlockhash(head, expectedBlockNumber, Frontier.Instance);
        Assert.That(result, Is.EqualTo(expected.Hash));
    }
}
