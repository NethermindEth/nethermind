// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Service;
using Nethermind.StateComposition.Snapshots;
using Nethermind.StateComposition.Visitors;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Service;

/// <summary>
/// Tests verifying content-addressed trie isolation guarantees.
///
/// Background: The StateComposition scan traverses the trie at a specific state root.
/// Because trie nodes are content-addressed (keyed by hash), new blocks that modify
/// state create NEW nodes with NEW hashes — they never overwrite existing nodes.
/// This gives natural snapshot isolation: a scan at root X always sees state X,
/// regardless of concurrent block processing at X+1..X+N.
///
/// These tests validate this guarantee at multiple levels:
/// 1. Direct trie isolation (StateTree + visitor)
/// 2. Service-layer isolation (StateCompositionService + real trie)
/// 3. Concurrent safety (scan + commits without deadlock)
/// </summary>
[TestFixture]
public class ScanConsistencyTests
{
    private static Account CreateEOA(int balance = 100) => new(0, (UInt256)balance);

    private static Account CreateContract(int balance = 0) =>
        new(0, (UInt256)balance, Keccak.EmptyTreeHash, Keccak.Compute([0x60, 0x00]));

    /// <summary>
    /// Core isolation property: scanning at root1 after committing root2
    /// still returns root1's account counts.
    ///
    /// Content-addressed nodes are keyed by hash — new blocks create
    /// NEW nodes with NEW hashes, never overwriting old ones.
    /// </summary>
    [Test]
    public void ScanAtOlderRoot_ReturnsOriginalAccountCounts_AfterNewBlocksCommitted()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Set(TestItem.AddressC, CreateContract());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressD, CreateEOA(300));
        tree.Set(TestItem.AddressE, CreateContract());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        StateCompositionStats stats1 = v1.GetStats(1, root1);

        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        StateCompositionStats stats2 = v2.GetStats(2, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root1, Is.Not.EqualTo(root2), "Roots must differ");

            Assert.That(stats1.AccountsTotal, Is.EqualTo(3), "Root1: 3 accounts");
            Assert.That(stats1.ContractsTotal, Is.EqualTo(1), "Root1: 1 contract");

            Assert.That(stats2.AccountsTotal, Is.EqualTo(5), "Root2: 5 accounts");
            Assert.That(stats2.ContractsTotal, Is.EqualTo(2), "Root2: 2 contracts");
        }
    }

    /// <summary>
    /// Isolation after in-place account modifications. When account A's balance
    /// changes, a new leaf node hash is created. The old root still points to
    /// the original leaf node hash → original account data.
    /// </summary>
    [Test]
    public void ScanAtOlderRoot_CorrectAfterAccountModification()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        tree.Set(TestItem.AddressA, new Account(1, 999));
        tree.Set(TestItem.AddressC, CreateEOA(300));
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        StateCompositionStats s1 = v1.GetStats(1, root1);

        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        StateCompositionStats s2 = v2.GetStats(2, root2);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(root1, Is.Not.EqualTo(root2));
            Assert.That(s1.AccountsTotal, Is.EqualTo(2), "Root1: 2 accounts");
            Assert.That(s2.AccountsTotal, Is.EqualTo(3), "Root2: 3 accounts");
        }
    }

    /// <summary>
    /// Trie structure (branch/extension/leaf node counts) differs between
    /// roots because adding accounts changes the tree layout. Verifies that
    /// node-level metrics are also isolated by content addressing.
    /// </summary>
    [Test]
    public void ScanAtOlderRoot_TrieNodeCounts_ReflectOriginalStructure()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        for (int i = 0; i < 20; i++)
        {
            tree.Set(TestItem.Addresses[i], CreateEOA(i));
        }

        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root2 = tree.RootHash;

        using StateCompositionVisitor v1 = new(LimboLogs.Instance);
        tree.Accept(v1, root1);
        StateCompositionStats s1 = v1.GetStats(1, root1);

        using StateCompositionVisitor v2 = new(LimboLogs.Instance);
        tree.Accept(v2, root2);
        StateCompositionStats s2 = v2.GetStats(2, root2);

        using (Assert.EnterMultipleScope())
        {
            long nodes1 = s1.AccountTrieFullNodes + s1.AccountTrieShortNodes;
            long nodes2 = s2.AccountTrieFullNodes + s2.AccountTrieShortNodes;
            Assert.That(nodes2, Is.GreaterThan(nodes1),
                "Larger state should produce more trie nodes");

            Assert.That(s2.AccountTrieNodeBytes, Is.GreaterThan(s1.AccountTrieNodeBytes),
                "Larger state should produce more trie bytes");
        }
    }

    /// <summary>
    /// Commits 5 sequential blocks, each adding one account.
    /// Verifies that scanning at ANY historical root produces the correct
    /// count for that point in time — proving all content-addressed snapshots
    /// remain independently readable.
    /// </summary>
    [Test]
    public void MultipleHistoricalRoots_AllScanCorrectly()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        Hash256[] roots = new Hash256[5];

        for (int block = 0; block < 5; block++)
        {
            tree.Set(TestItem.Addresses[block], CreateEOA(block * 100));
            tree.Commit();
            tree.UpdateRootHash();
            roots[block] = tree.RootHash;
        }

        for (int block = 0; block < 5; block++)
        {
            using StateCompositionVisitor v = new(LimboLogs.Instance);
            tree.Accept(v, roots[block]);
            StateCompositionStats stats = v.GetStats(block, roots[block]);
            Assert.That(stats.AccountsTotal, Is.EqualTo(block + 1),
                $"Root at block {block} should see {block + 1} accounts");
        }
    }

    /// <summary>
    /// Validates that RunTreeVisitor (pure read, no _pruningLock acquisition)
    /// does not block or deadlock with concurrent trie writes.
    ///
    /// Uses two separate StateTree instances sharing the same backing store,
    /// mirroring the real architecture where StateReader and WorldState have
    /// independent tree instances over a shared TrieStore/DB.
    /// </summary>
    [Test]
    [CancelAfter(15_000)]
    public async Task ConcurrentScanAndCommit_BothComplete_NoDegradation()
    {
        MemDb db = new();
        StateTree readTree = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        StateTree writeTree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        for (int i = 0; i < 50; i++)
        {
            writeTree.Set(TestItem.Addresses[i], CreateEOA(i));
        }

        writeTree.Commit();
        writeTree.UpdateRootHash();
        Hash256 root1 = writeTree.RootHash;

        ManualResetEventSlim scanStarted = new(false);

        Task<StateCompositionStats> scanTask = Task.Run(() =>
        {
            using StateCompositionVisitor v = new(LimboLogs.Instance);
            scanStarted.Set();
            readTree.Accept(v, root1);
            return v.GetStats(1, root1);
        });

        scanStarted.Wait(TimeSpan.FromSeconds(5));
        for (int i = 50; i < 100; i++)
        {
            writeTree.Set(TestItem.Addresses[i], CreateEOA(i));
        }

        writeTree.Commit();

        // Both must complete within CancelAfter timeout (no deadlock)
        StateCompositionStats stats = await scanTask;
        Assert.That(stats.AccountsTotal, Is.EqualTo(50),
            "Scan at root1 should see exactly 50 accounts from block 1");
    }

    /// <summary>
    /// End-to-end through StateCompositionService: proves the full
    /// Service → Visitor → TreeTraversal chain respects content-addressed
    /// isolation when scanning at different historical roots.
    /// </summary>
    [Test]
    public async Task ServiceLayer_ScanAtDifferentRoots_ProducesDistinctResults()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        // Block 0: 3 accounts (2 EOA + 1 contract)
        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Set(TestItem.AddressB, CreateEOA(200));
        tree.Set(TestItem.AddressC, CreateContract());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root0 = tree.RootHash;

        // Block 1: +2 = 5 accounts (3 EOA + 2 contracts)
        tree.Set(TestItem.AddressD, CreateEOA(400));
        tree.Set(TestItem.AddressE, CreateContract());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        // Wire IStateReader to delegate RunTreeVisitor to the real StateTree
        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(null!, null))
            .Do(callInfo =>
            {
                var visitor = (ITreeVisitor<StateCompositionContext>)callInfo[0];
                var header = (BlockHeader?)callInfo[1];
                Hash256 root = header?.StateRoot ?? Keccak.EmptyTreeHash;
                tree.Accept(visitor, root);
            });

        StateCompositionStateHolder stateHolder = new();
        using StateCompositionService service = new(
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            stateHolder, new StateCompositionSnapshotStore(new MemDb(), LimboLogs.Instance),
            new StateCompositionConfig { ScanParallelism = 1 },
            LimboLogs.Instance);

        BlockHeader header0 = Build.A.BlockHeader
            .WithNumber(0).WithStateRoot(root0).TestObject;
        Result<StateCompositionStats> result0 =
            await service.AnalyzeAsync(header0, CancellationToken.None);

        BlockHeader header1 = Build.A.BlockHeader
            .WithNumber(1).WithStateRoot(root1).TestObject;
        Result<StateCompositionStats> result1 =
            await service.AnalyzeAsync(header1, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result0.IsSuccess, Is.True, $"Scan at root0 failed: {result0.Error}");
            Assert.That(result1.IsSuccess, Is.True, $"Scan at root1 failed: {result1.Error}");

            Assert.That(result0.Data.AccountsTotal, Is.EqualTo(3), "Root0: 3 accounts");
            Assert.That(result0.Data.ContractsTotal, Is.EqualTo(1), "Root0: 1 contract");

            Assert.That(result1.Data.AccountsTotal, Is.EqualTo(5), "Root1: 5 accounts");
            Assert.That(result1.Data.ContractsTotal, Is.EqualTo(2), "Root1: 2 contracts");
        }
    }

    /// <summary>
    /// Verifies that after a scan completes and updates the state holder,
    /// a subsequent scan at a different root overwrites the cached stats
    /// with the new root's data — proving sequential scan correctness.
    /// </summary>
    [Test]
    public async Task SequentialScans_StateHolderUpdatedWithLatestRoot()
    {
        MemDb db = new();
        StateTree tree = new(new RawScopedTrieStore(db), LimboLogs.Instance);

        tree.Set(TestItem.AddressA, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root0 = tree.RootHash;

        tree.Set(TestItem.AddressB, CreateEOA());
        tree.Set(TestItem.AddressC, CreateEOA());
        tree.Commit();
        tree.UpdateRootHash();
        Hash256 root1 = tree.RootHash;

        IStateReader stateReader = Substitute.For<IStateReader>();
        stateReader.WhenForAnyArgs(x =>
                x.RunTreeVisitor<StateCompositionContext>(null!, null))
            .Do(callInfo =>
            {
                var visitor = (ITreeVisitor<StateCompositionContext>)callInfo[0];
                var header = (BlockHeader?)callInfo[1];
                tree.Accept(visitor, header?.StateRoot ?? Keccak.EmptyTreeHash);
            });

        StateCompositionStateHolder stateHolder = new();
        using StateCompositionService service = new(
            stateReader, Substitute.For<IWorldStateManager>(), Substitute.For<IBlockTree>(),
            stateHolder, new StateCompositionSnapshotStore(new MemDb(), LimboLogs.Instance),
            new StateCompositionConfig { ScanParallelism = 1 },
            LimboLogs.Instance);

        await service.AnalyzeAsync(
            Build.A.BlockHeader.WithNumber(0).WithStateRoot(root0).TestObject,
            CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateHolder.CurrentStats.AccountsTotal, Is.EqualTo(1));
            Assert.That(stateHolder.LastScanMetadata.BlockNumber, Is.Zero);
        }

        await service.AnalyzeAsync(
            Build.A.BlockHeader.WithNumber(1).WithStateRoot(root1).TestObject,
            CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(stateHolder.CurrentStats.AccountsTotal, Is.EqualTo(3));
            Assert.That(stateHolder.LastScanMetadata.BlockNumber, Is.EqualTo(1));
        }
    }
}
