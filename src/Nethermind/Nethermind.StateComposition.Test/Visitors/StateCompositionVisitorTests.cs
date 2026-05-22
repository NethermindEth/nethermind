// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Visitors;

[TestFixture]
public class StateCompositionVisitorTests
{
    private StateCompositionVisitor _visitor;

    [SetUp]
    public void SetUp() => _visitor = new StateCompositionVisitor(LimboLogs.Instance);

    [TearDown]
    public void TearDown() => _visitor.Dispose();

    [Test]
    public void Visitor_ShouldVisit_TracksBranchChildren()
    {
        ValueHash256 hash = default;
        StateCompositionContext ctx1 = new(default, level: 1, isStorage: false, branchChildIndex: 0);
        StateCompositionContext ctx2 = new(default, level: 1, isStorage: false, branchChildIndex: 3);
        StateCompositionContext ctx3 = new(default, level: 1, isStorage: false, branchChildIndex: 7);

        _visitor.ShouldVisit(in ctx1, in hash);
        _visitor.ShouldVisit(in ctx2, in hash);
        _visitor.ShouldVisit(in ctx3, in hash);

        // Create a branch node to trigger TotalBranchNodes count.
        // Use valid empty-branch RLP (17 × 0x80 = all-null children + null value).
        TrieNode branchNode = new(NodeType.Branch, EmptyBranchRlp());
        StateCompositionContext branchCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        _visitor.VisitBranch(in branchCtx, branchNode);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.AvgBranchOccupancy, Is.EqualTo(3.0));
    }

    // Two SimulateAccounts batches per case: (count, hasCode, hasStorage) × 2, then expected totals.
    [TestCase(5, false, false, 5, true, false, 10, 5, 0, TestName = "ClassifiesContracts_EOAsVsContracts")]
    [TestCase(3, true, true, 2, true, false, 5, 5, 3, TestName = "TracksContractsWithStorage")]
    public void Visitor_ClassifiesAccounts(
        int n1, bool code1, bool storage1,
        int n2, bool code2, bool storage2,
        int expectedAccounts, int expectedContracts, int expectedWithStorage)
    {
        SimulateAccounts(n1, hasCode: code1, hasStorage: storage1);
        SimulateAccounts(n2, hasCode: code2, hasStorage: storage2);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(expectedAccounts));
            Assert.That(stats.ContractsTotal, Is.EqualTo(expectedContracts));
            Assert.That(stats.ContractsWithStorage, Is.EqualTo(expectedWithStorage));
        }
    }

    [Test]
    public void Visitor_SeparatesAccountAndStorageTries()
    {
        TrieNode node = new(NodeType.Branch, EmptyBranchRlp());

        StateCompositionContext accountCtx = new(default, level: 1, isStorage: false, branchChildIndex: null);
        StateCompositionContext storageCtx = new(default, level: 1, isStorage: true, branchChildIndex: null);

        _visitor.VisitBranch(in accountCtx, node);
        _visitor.VisitBranch(in accountCtx, node);
        _visitor.VisitBranch(in storageCtx, node);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountTrieFullNodes, Is.EqualTo(2));
            Assert.That(stats.StorageTrieFullNodes, Is.EqualTo(1));
        }
    }

    [Test]
    public void Visitor_TracksDepthDistribution()
    {
        TrieNode node = new(NodeType.Branch, EmptyBranchRlp());

        for (int depth = 0; depth < 3; depth++)
        {
            StateCompositionContext ctx = new(default, level: depth, isStorage: false, branchChildIndex: null);
            _visitor.VisitBranch(in ctx, node);
        }

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dist.AccountTrieLevels, Has.Length.EqualTo(3));
            Assert.That(dist.MaxAccountDepth, Is.EqualTo(2));
        }

    }

    [Test]
    public void Visitor_TracksByteSizes()
    {
        TrieNode node = new(NodeType.Branch, EmptyBranchRlp());

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        _visitor.VisitBranch(in ctx, node);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.AccountTrieNodeBytes, Is.GreaterThan(0));
    }

    [Test]
    public void Visitor_ClampsDepthAtMaxIndex()
    {
        TrieNode node = new(NodeType.Leaf, [0xc0]);

        StateCompositionContext ctx = new(default, level: 20, isStorage: false, branchChildIndex: null);
        _visitor.VisitLeaf(in ctx, node);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        // Depth 20 should be clamped to MaxTrackedDepth - 1 = 15
        Assert.That(dist.AccountTrieLevels, Has.Length.EqualTo(1));
        Assert.That(dist.AccountTrieLevels[0].Depth, Is.EqualTo(VisitorCounters.MaxTrackedDepth - 1));
    }

    [Test]
    public void Visitor_CountsStorageSlots()
    {
        TrieNode node = new(NodeType.Leaf, [0xc0, 0x01]);

        StateCompositionContext storageCtx = new(default, level: 3, isStorage: true, branchChildIndex: null);

        for (int i = 0; i < 30; i++)
            _visitor.VisitLeaf(in storageCtx, node);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.StorageSlotsTotal, Is.EqualTo(30));
    }

    [Test]
    public void Visitor_ShouldVisit_ReturnsFalse_WhenCancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        using StateCompositionVisitor cancelled = new(LimboLogs.Instance, ct: cts.Token);

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        Assert.That(cancelled.ShouldVisit(in ctx, in hash), Is.False);
    }

    [Test]
    public void Visitor_TracksTopContractsByDepth()
    {
        // Visit 3 accounts with storage, each with different depth storage nodes
        TrieNode leafNode = new(NodeType.Leaf, [0xc0, 0x01]);
        StateCompositionContext storageCtx3 = new(default, level: 3, isStorage: true, branchChildIndex: null);
        StateCompositionContext storageCtx7 = new(default, level: 7, isStorage: true, branchChildIndex: null);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        AccountStruct acc1 = new(0, 0, Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in acc1);
        _visitor.VisitLeaf(in storageCtx3, leafNode);

        AccountStruct acc2 = new(0, 0, Keccak.Compute("root2").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in acc2);
        _visitor.VisitLeaf(in storageCtx7, leafNode);

        AccountStruct acc3 = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in acc3);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.TopContractsByDepth, Has.Length.EqualTo(2));
        Assert.That(stats.TopContractsByDepth[0].MaxDepth, Is.EqualTo(8)); // Sorted descending (+1 Geth convention)
    }

    [Test]
    public void Visitor_StorageMaxDepthHistogram_PopulatedCorrectly()
    {
        TrieNode leafNode = new(NodeType.Leaf, [0xc0, 0x01]);
        StateCompositionContext storageCtx = new(default, level: 4, isStorage: true, branchChildIndex: null);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // 2 accounts with storage tries, each with max depth 4
        for (int i = 0; i < 2; i++)
        {
            AccountStruct acc = new(0, 0, Keccak.Compute($"root{i}").ValueHash256, Keccak.Zero.ValueHash256);
            _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in acc);
            _visitor.VisitLeaf(in storageCtx, leafNode);
        }

        // Flush last account via non-storage account
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in eoa);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.StorageMaxDepthHistogram, Has.Length.EqualTo(VisitorCounters.MaxTrackedDepth));
        Assert.That(dist.StorageMaxDepthHistogram[5], Is.EqualTo(2)); // bucket 5 = raw depth 4 + 1 (Geth convention)
    }

    [Test]
    public void Visitor_ExcludeStorage_SkipsPerContractTracking()
    {
        using StateCompositionVisitor visitor = new(LimboLogs.Instance, excludeStorage: true);

        TrieNode node = new(NodeType.Leaf, [0xc0, 0x01]);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Visit account that has storage — ExcludeStorage calls Flush() instead of BeginStorageTrie()
        AccountStruct accWithStorage = new(0, 0, Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in accWithStorage);

        // Visit a storage leaf (tree infrastructure still descends)
        StateCompositionContext storageCtx = new(default, level: 2, isStorage: true, branchChildIndex: null);
        visitor.VisitLeaf(in storageCtx, node);

        // Flush via a non-storage account
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in eoa);

        StateCompositionStats stats = visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(2));
            Assert.That(stats.ContractsWithStorage, Is.Zero); // Not tracked in ExcludeStorage mode
            Assert.That(stats.TopContractsByDepth, Has.Length.EqualTo(0)); // No per-contract tracking
            Assert.That(stats.StorageSlotsTotal, Is.EqualTo(1)); // Storage nodes still counted globally
        }
    }

    [Test]
    public void Visitor_CountsEmptyAccounts()
    {
        TrieNode node = new(NodeType.Leaf, [0xc0]);
        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Totally empty: zero balance, zero nonce, no code, no storage
        AccountStruct empty = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Not empty: has balance
        AccountStruct withBalance = new(0, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);

        _visitor.VisitAccount(in ctx, node, in empty);
        _visitor.VisitAccount(in ctx, node, in empty);
        _visitor.VisitAccount(in ctx, node, in withBalance);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(3));
            Assert.That(stats.EmptyAccounts, Is.EqualTo(2));
        }
    }

    [Test]
    public void Visitor_BranchOccupancyDistribution_HasCorrectShape()
    {
        // Use valid empty-branch RLP so IsChildNull can decode correctly.
        TrieNode branchNode = new(NodeType.Branch, EmptyBranchRlp());

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        _visitor.VisitBranch(in ctx, branchNode);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.BranchOccupancyDistribution, Has.Length.EqualTo(VisitorCounters.MaxTrackedDepth));
    }

    [Test]
    public void Visitor_TopContractsBySize_RankedCorrectly()
    {
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Contract 1: small storage (1 leaf of 2 bytes)
        byte[] smallRlp = [0xc0, 0x01];
        TrieNode smallLeaf = new(NodeType.Leaf, smallRlp);
        StateCompositionContext storageCtx = new(default, level: 2, isStorage: true, branchChildIndex: null);

        AccountStruct acc1 = new(0, 0, Keccak.Compute("root1").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in acc1);
        _visitor.VisitLeaf(in storageCtx, smallLeaf);

        // Contract 2: larger storage (3 leaves of 50 bytes each)
        byte[] bigRlp = new byte[50];
        bigRlp[0] = 0xc0;
        TrieNode bigLeaf = new(NodeType.Leaf, bigRlp);

        AccountStruct acc2 = new(0, 0, Keccak.Compute("root2").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in acc2);
        _visitor.VisitLeaf(in storageCtx, bigLeaf);
        _visitor.VisitLeaf(in storageCtx, bigLeaf);
        _visitor.VisitLeaf(in storageCtx, bigLeaf);

        // Flush
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, [0xc0]), in eoa);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.TopContractsBySize, Has.Length.EqualTo(2));
            // Sorted descending by size — contract 2 (3x50=150) > contract 1 (1x2=2)
            Assert.That(stats.TopContractsBySize[0].TotalSize, Is.GreaterThan(stats.TopContractsBySize[1].TotalSize));
        }
    }

    /// <summary>
    /// Returns a valid RLP-encoded empty branch node (17 × 0x80).
    /// Needed so that <see cref="TrieNode.IsChildNull"/> can decode without throwing.
    /// </summary>
    private static byte[] EmptyBranchRlp()
    {
        // 17 items of 0x80 (RLP null/empty), list length = 17, short-list prefix = 0xC0 + 17 = 0xD1
        byte[] rlp = new byte[18];
        rlp[0] = 0xD1;
        for (int i = 1; i <= 17; i++) rlp[i] = 0x80;
        return rlp;
    }

    private void SimulateAccounts(int count, bool hasCode, bool hasStorage)
    {
        TrieNode node = new(NodeType.Leaf, [0xc0]);

        AccountStruct account = new(
            nonce: 0,
            balance: 0,
            storageRoot: hasStorage ? Keccak.Zero.ValueHash256 : Keccak.EmptyTreeHash.ValueHash256,
            codeHash: hasCode ? Keccak.Zero.ValueHash256 : Keccak.OfAnEmptyString.ValueHash256
        );

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        for (int i = 0; i < count; i++)
            _visitor.VisitAccount(in ctx, node, in account);
    }
}
