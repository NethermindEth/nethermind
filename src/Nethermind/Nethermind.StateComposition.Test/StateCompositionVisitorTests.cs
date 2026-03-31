// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class StateCompositionVisitorTests
{
    private StateCompositionVisitor _visitor = null!;

    [SetUp]
    public void SetUp()
    {
        _visitor = new StateCompositionVisitor(LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _visitor.Dispose();
    }

    [Test]
    public void Visitor_IsFullDbScan()
    {
        Assert.That(_visitor.IsFullDbScan, Is.True);
    }

    [Test]
    public void Visitor_ExtraReadFlag_IsHintCacheMiss()
    {
        Assert.That(_visitor.ExtraReadFlag, Is.EqualTo(ReadFlags.HintCacheMiss));
    }

    [Test]
    public void Visitor_ExpectAccounts()
    {
        Assert.That(_visitor.ExpectAccounts, Is.True);
    }

    [Test]
    public void Visitor_ShouldVisit_AlwaysReturnsTrue()
    {
        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        Assert.That(_visitor.ShouldVisit(in ctx, in hash), Is.True);
    }

    [Test]
    public void Visitor_ShouldVisit_TracksBranchChildren()
    {
        // Simulate 3 branch children being visited
        ValueHash256 hash = default;
        StateCompositionContext ctx1 = new(default, level: 1, isStorage: false, branchChildIndex: 0);
        StateCompositionContext ctx2 = new(default, level: 1, isStorage: false, branchChildIndex: 3);
        StateCompositionContext ctx3 = new(default, level: 1, isStorage: false, branchChildIndex: 7);

        _visitor.ShouldVisit(in ctx1, in hash);
        _visitor.ShouldVisit(in ctx2, in hash);
        _visitor.ShouldVisit(in ctx3, in hash);

        // Create a branch node to trigger TotalBranchNodes count
        TrieNode branchNode = new(NodeType.Branch, new byte[] { 0xc0 });
        StateCompositionContext branchCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        _visitor.VisitBranch(in branchCtx, branchNode);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.AvgBranchOccupancy, Is.EqualTo(3.0));
    }

    [Test]
    public void Visitor_CountsAccountsCorrectly()
    {
        SimulateAccounts(10, hasCode: false, hasStorage: false);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.AccountsTotal, Is.EqualTo(10));
    }

    [Test]
    public void Visitor_ClassifiesContracts()
    {
        SimulateAccounts(5, hasCode: false, hasStorage: false);
        SimulateAccounts(5, hasCode: true, hasStorage: false);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(10));
            Assert.That(stats.ContractsTotal, Is.EqualTo(5));
        });
    }

    [Test]
    public void Visitor_TracksContractsWithStorage()
    {
        SimulateAccounts(3, hasCode: true, hasStorage: true);
        SimulateAccounts(2, hasCode: true, hasStorage: false);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.ContractsTotal, Is.EqualTo(5));
            Assert.That(stats.ContractsWithStorage, Is.EqualTo(3));
        });
    }

    [Test]
    public void Visitor_SeparatesAccountAndStorageTries()
    {
        TrieNode node = new(NodeType.Branch, new byte[] { 0xc0, 0x01, 0x02, 0x03 });

        StateCompositionContext accountCtx = new(default, level: 1, isStorage: false, branchChildIndex: null);
        StateCompositionContext storageCtx = new(default, level: 1, isStorage: true, branchChildIndex: null);

        _visitor.VisitBranch(in accountCtx, node);
        _visitor.VisitBranch(in accountCtx, node);
        _visitor.VisitBranch(in storageCtx, node);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountTrieFullNodes, Is.EqualTo(2));
            Assert.That(stats.StorageTrieFullNodes, Is.EqualTo(1));
        });
    }

    [Test]
    public void Visitor_TracksDepthDistribution()
    {
        TrieNode node = new(NodeType.Branch, new byte[] { 0xc0, 0x01 });

        // Visit branches at depths 0, 1, 2
        for (int depth = 0; depth < 3; depth++)
        {
            StateCompositionContext ctx = new(default, level: depth, isStorage: false, branchChildIndex: null);
            _visitor.VisitBranch(in ctx, node);
        }

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.AccountTrieLevels, Has.Length.EqualTo(3));
        Assert.That(dist.MaxAccountDepth, Is.EqualTo(2));
    }

    [Test]
    public void Visitor_TracksByteSizes()
    {
        byte[] rlp = new byte[100];
        rlp[0] = 0xc0;
        TrieNode node = new(NodeType.Branch, rlp);

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        _visitor.VisitBranch(in ctx, node);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.AccountTrieNodeBytes, Is.GreaterThan(0));
    }

    [Test]
    public void Visitor_ThreadLocalAggregation()
    {
        // Simulate multi-threaded access by visiting from main thread
        // ThreadLocal creates separate counters per thread
        SimulateAccounts(5, hasCode: true, hasStorage: false);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(5));
            Assert.That(stats.ContractsTotal, Is.EqualTo(5));
        });
    }

    [Test]
    public void Visitor_DisposesThreadLocal()
    {
        _visitor.Dispose();

        // After dispose, accessing _localCounters.Values would throw
        // Just verify no exception on dispose itself
        Assert.Pass("Dispose completed without error");
    }

    [Test]
    public void Visitor_ClampsDepthAtMaxIndex()
    {
        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0 });

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
        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0, 0x01 });

        StateCompositionContext storageCtx = new(default, level: 3, isStorage: true, branchChildIndex: null);

        for (int i = 0; i < 30; i++)
        {
            _visitor.VisitLeaf(in storageCtx, node);
        }

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.StorageSlotsTotal, Is.EqualTo(30));
    }

    [Test]
    public void Visitor_ShouldVisit_ReturnsFalse_WhenCancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        using StateCompositionVisitor cancelled = new(LimboLogs.Instance, cts.Token);

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        Assert.That(cancelled.ShouldVisit(in ctx, in hash), Is.False);
    }

    [Test]
    public void Visitor_TracksTopContractsByDepth()
    {
        // Visit 3 accounts with storage, each with different depth storage nodes
        TrieNode leafNode = new(NodeType.Leaf, new byte[] { 0xc0, 0x01 });
        StateCompositionContext storageCtx3 = new(default, level: 3, isStorage: true, branchChildIndex: null);
        StateCompositionContext storageCtx7 = new(default, level: 7, isStorage: true, branchChildIndex: null);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Account 1: storage max depth 3
        AccountStruct acc1 = new(0, 0, Keccak.Zero.ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc1);
        _visitor.VisitLeaf(in storageCtx3, leafNode);

        // Account 2: storage max depth 7
        AccountStruct acc2 = new(0, 0, Keccak.Compute("root2").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc2);
        _visitor.VisitLeaf(in storageCtx7, leafNode);

        // Account 3: no storage (flushes acc2)
        AccountStruct acc3 = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc3);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.That(stats.TopContractsByDepth, Has.Length.EqualTo(2));
        Assert.That(stats.TopContractsByDepth[0].MaxDepth, Is.EqualTo(8)); // Sorted descending (+1 Geth convention)
    }

    [Test]
    public void Visitor_StorageMaxDepthHistogram_PopulatedCorrectly()
    {
        TrieNode leafNode = new(NodeType.Leaf, new byte[] { 0xc0, 0x01 });
        StateCompositionContext storageCtx = new(default, level: 4, isStorage: true, branchChildIndex: null);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // 2 accounts with storage tries, each with max depth 4
        for (int i = 0; i < 2; i++)
        {
            AccountStruct acc = new(0, 0, Keccak.Compute($"root{i}").ValueHash256, Keccak.Zero.ValueHash256);
            _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc);
            _visitor.VisitLeaf(in storageCtx, leafNode);
        }

        // Flush last account via non-storage account
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in eoa);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.StorageMaxDepthHistogram, Has.Length.EqualTo(VisitorCounters.MaxTrackedDepth));
        Assert.That(dist.StorageMaxDepthHistogram[5], Is.EqualTo(2)); // bucket 5 = raw depth 4 + 1 (Geth convention)
    }

    [Test]
    public void Visitor_ExcludeStorage_SkipsPerContractTracking()
    {
        using StateCompositionVisitor visitor = new(LimboLogs.Instance, excludeStorage: true);

        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0, 0x01 });
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

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(2));
            Assert.That(stats.ContractsWithStorage, Is.EqualTo(0)); // Not tracked in ExcludeStorage mode
            Assert.That(stats.TopContractsByDepth, Has.Length.EqualTo(0)); // No per-contract tracking
            Assert.That(stats.StorageSlotsTotal, Is.EqualTo(1)); // Storage nodes still counted globally
        });
    }

    [Test]
    public void Visitor_CountsEmptyAccounts()
    {
        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0 });
        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Totally empty: zero balance, zero nonce, no code, no storage
        AccountStruct empty = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Not empty: has balance
        AccountStruct withBalance = new(0, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);

        _visitor.VisitAccount(in ctx, node, in empty);
        _visitor.VisitAccount(in ctx, node, in empty);
        _visitor.VisitAccount(in ctx, node, in withBalance);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.AccountsTotal, Is.EqualTo(3));
            Assert.That(stats.EmptyAccounts, Is.EqualTo(2));
        });
    }

    [Test]
    public void Visitor_BalanceDistribution_BucketsCorrectly()
    {
        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0 });
        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Bucket 0: zero balance
        AccountStruct b0 = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 1: < 0.01 ETH (1 Wei)
        AccountStruct b1 = new(0, 1, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 2: 0.01-1 ETH (0.5 ETH = 5*10^17)
        AccountStruct b2 = new(0, UInt256.Parse("500000000000000000"), Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 3: 1-10 ETH (5 ETH = 5*10^18)
        AccountStruct b3 = new(0, UInt256.Parse("5000000000000000000"), Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 7: 10K+ ETH (50K ETH = 5*10^22)
        AccountStruct b7 = new(0, UInt256.Parse("50000000000000000000000"), Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);

        _visitor.VisitAccount(in ctx, node, in b0);
        _visitor.VisitAccount(in ctx, node, in b1);
        _visitor.VisitAccount(in ctx, node, in b2);
        _visitor.VisitAccount(in ctx, node, in b3);
        _visitor.VisitAccount(in ctx, node, in b7);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.Multiple(() =>
        {
            Assert.That(dist.BalanceDistribution, Has.Length.EqualTo(VisitorCounters.BalanceBucketCount));
            Assert.That(dist.BalanceDistribution[0], Is.EqualTo(1), "Bucket 0: zero");
            Assert.That(dist.BalanceDistribution[1], Is.EqualTo(1), "Bucket 1: <0.01 ETH");
            Assert.That(dist.BalanceDistribution[2], Is.EqualTo(1), "Bucket 2: 0.01-1 ETH");
            Assert.That(dist.BalanceDistribution[3], Is.EqualTo(1), "Bucket 3: 1-10 ETH");
            Assert.That(dist.BalanceDistribution[7], Is.EqualTo(1), "Bucket 7: 10K+ ETH");
        });
    }

    [Test]
    public void Visitor_NonceDistribution_BucketsCorrectly()
    {
        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0 });
        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Bucket 0: nonce 0
        AccountStruct n0 = new(0, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 1: nonce 1
        AccountStruct n1 = new(1, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 2: nonce 2-10
        AccountStruct n5 = new(5, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 3: nonce 11-100
        AccountStruct n50 = new(50, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 4: nonce 101-1000
        AccountStruct n500 = new(500, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        // Bucket 5: nonce 1000+
        AccountStruct n5000 = new(5000, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);

        _visitor.VisitAccount(in ctx, node, in n0);
        _visitor.VisitAccount(in ctx, node, in n1);
        _visitor.VisitAccount(in ctx, node, in n5);
        _visitor.VisitAccount(in ctx, node, in n50);
        _visitor.VisitAccount(in ctx, node, in n500);
        _visitor.VisitAccount(in ctx, node, in n5000);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.Multiple(() =>
        {
            Assert.That(dist.NonceDistribution, Has.Length.EqualTo(VisitorCounters.NonceBucketCount));
            Assert.That(dist.NonceDistribution[0], Is.EqualTo(1), "Bucket 0: nonce=0");
            Assert.That(dist.NonceDistribution[1], Is.EqualTo(1), "Bucket 1: nonce=1");
            Assert.That(dist.NonceDistribution[2], Is.EqualTo(1), "Bucket 2: nonce 2-10");
            Assert.That(dist.NonceDistribution[3], Is.EqualTo(1), "Bucket 3: nonce 11-100");
            Assert.That(dist.NonceDistribution[4], Is.EqualTo(1), "Bucket 4: nonce 101-1K");
            Assert.That(dist.NonceDistribution[5], Is.EqualTo(1), "Bucket 5: nonce 1K+");
        });
    }

    [Test]
    public void Visitor_BranchOccupancyDistribution_HasCorrectShape()
    {
        // Stub TrieNodes don't have fully decoded RLP, so IsChildNull will be
        // caught by the try-catch guard. This test verifies the distribution
        // array is correctly shaped and returned via GetTrieDistribution().
        byte[] rlp = new byte[] { 0xc0 };
        TrieNode branchNode = new(NodeType.Branch, rlp);

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        _visitor.VisitBranch(in ctx, branchNode);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.That(dist.BranchOccupancyDistribution, Has.Length.EqualTo(VisitorCounters.MaxTrackedDepth));
    }

    [Test]
    public void Visitor_StorageSlotDistribution_PopulatedCorrectly()
    {
        TrieNode leafNode = new(NodeType.Leaf, new byte[] { 0xc0, 0x01 });
        StateCompositionContext storageCtx = new(default, level: 2, isStorage: true, branchChildIndex: null);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Contract 1: 1 storage slot → bucket 0
        AccountStruct acc1 = new(0, 0, Keccak.Compute("root1").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc1);
        _visitor.VisitLeaf(in storageCtx, leafNode);

        // Contract 2: 5 storage slots → bucket 1 (2-10)
        AccountStruct acc2 = new(0, 0, Keccak.Compute("root2").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc2);
        for (int i = 0; i < 5; i++)
            _visitor.VisitLeaf(in storageCtx, leafNode);

        // Flush via non-storage account
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in eoa);

        TrieDepthDistribution dist = _visitor.GetTrieDistribution();

        Assert.Multiple(() =>
        {
            Assert.That(dist.StorageSlotDistribution, Has.Length.EqualTo(VisitorCounters.StorageSlotBucketCount));
            Assert.That(dist.StorageSlotDistribution[0], Is.EqualTo(1), "Bucket 0: 1 slot");
            Assert.That(dist.StorageSlotDistribution[1], Is.EqualTo(1), "Bucket 1: 2-10 slots");
        });
    }

    [Test]
    public void Visitor_TopContractsBySize_RankedCorrectly()
    {
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        // Contract 1: small storage (1 leaf of 2 bytes)
        byte[] smallRlp = new byte[] { 0xc0, 0x01 };
        TrieNode smallLeaf = new(NodeType.Leaf, smallRlp);
        StateCompositionContext storageCtx = new(default, level: 2, isStorage: true, branchChildIndex: null);

        AccountStruct acc1 = new(0, 0, Keccak.Compute("root1").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc1);
        _visitor.VisitLeaf(in storageCtx, smallLeaf);

        // Contract 2: larger storage (3 leaves of 50 bytes each)
        byte[] bigRlp = new byte[50];
        bigRlp[0] = 0xc0;
        TrieNode bigLeaf = new(NodeType.Leaf, bigRlp);

        AccountStruct acc2 = new(0, 0, Keccak.Compute("root2").ValueHash256, Keccak.Zero.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in acc2);
        _visitor.VisitLeaf(in storageCtx, bigLeaf);
        _visitor.VisitLeaf(in storageCtx, bigLeaf);
        _visitor.VisitLeaf(in storageCtx, bigLeaf);

        // Flush
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        _visitor.VisitAccount(in accountCtx, new TrieNode(NodeType.Leaf, new byte[] { 0xc0 }), in eoa);

        StateCompositionStats stats = _visitor.GetStats(1, null);

        Assert.Multiple(() =>
        {
            Assert.That(stats.TopContractsBySize, Has.Length.EqualTo(2));
            // Sorted descending by size — contract 2 (3×50=150) > contract 1 (1×2=2)
            Assert.That(stats.TopContractsBySize[0].TotalSize, Is.GreaterThan(stats.TopContractsBySize[1].TotalSize));
        });
    }

    [Test]
    public void SlotBucket_BoundaryValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(VisitorCounters.SlotBucket(0), Is.EqualTo(0));
            Assert.That(VisitorCounters.SlotBucket(1), Is.EqualTo(0));
            Assert.That(VisitorCounters.SlotBucket(2), Is.EqualTo(1));
            Assert.That(VisitorCounters.SlotBucket(10), Is.EqualTo(1));
            Assert.That(VisitorCounters.SlotBucket(11), Is.EqualTo(2));
            Assert.That(VisitorCounters.SlotBucket(100), Is.EqualTo(2));
            Assert.That(VisitorCounters.SlotBucket(101), Is.EqualTo(3));
            Assert.That(VisitorCounters.SlotBucket(1000), Is.EqualTo(3));
            Assert.That(VisitorCounters.SlotBucket(1001), Is.EqualTo(4));
            Assert.That(VisitorCounters.SlotBucket(10000), Is.EqualTo(4));
            Assert.That(VisitorCounters.SlotBucket(10001), Is.EqualTo(5));
            Assert.That(VisitorCounters.SlotBucket(100000), Is.EqualTo(5));
            Assert.That(VisitorCounters.SlotBucket(100001), Is.EqualTo(6));
            Assert.That(VisitorCounters.SlotBucket(1000000), Is.EqualTo(6));
        });
    }

    private void SimulateAccounts(int count, bool hasCode, bool hasStorage)
    {
        TrieNode node = new(NodeType.Leaf, new byte[] { 0xc0 });

        AccountStruct account = new(
            nonce: 0,
            balance: 0,
            storageRoot: hasStorage ? Keccak.Zero.ValueHash256 : Keccak.EmptyTreeHash.ValueHash256,
            codeHash: hasCode ? Keccak.Zero.ValueHash256 : Keccak.OfAnEmptyString.ValueHash256
        );

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        for (int i = 0; i < count; i++)
        {
            _visitor.VisitAccount(in ctx, node, in account);
        }
    }
}
