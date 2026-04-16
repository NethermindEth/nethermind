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

/// <summary>
/// SingleContractVisitor unit tests — verify targeted single-contract
/// storage inspection logic including Geth conventions.
/// </summary>
[TestFixture]
public class SingleContractVisitorTests
{
    [Test]
    public void GetResult_ReturnsNull_WhenTargetNotFound()
    {
        using SingleContractVisitor visitor = new(
            LimboLogs.Instance, targetStorageRoot: default, ct: CancellationToken.None);

        TrieNode node = new(NodeType.Leaf, [0xc0]);
        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        AccountStruct eoa = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        visitor.VisitAccount(in ctx, node, in eoa);

        TopContractEntry? result = visitor.GetResult(default, default);

        Assert.That(result, Is.Null);
    }

    [Test]
    public void GetResult_CollectsStats_WhenTargetFound()
    {
        ValueHash256 targetRoot = Keccak.Compute("target-root").ValueHash256;

        using SingleContractVisitor visitor = new(
            LimboLogs.Instance, targetStorageRoot: targetRoot, ct: CancellationToken.None);

        TrieNode node = new(NodeType.Leaf, [0xc0]);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);

        AccountStruct targetAccount = new(0, 0, targetRoot, Keccak.Zero.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in targetAccount);

        byte[] storageRlp = [0xc0, 0x01, 0x02, 0x03];
        TrieNode branchNode = new(NodeType.Branch, storageRlp);
        TrieNode leafNode = new(NodeType.Leaf, [0xc0, 0x01]);

        StateCompositionContext storCtx0 = new(default, level: 0, isStorage: true, branchChildIndex: null);
        StateCompositionContext storCtx1 = new(default, level: 1, isStorage: true, branchChildIndex: null);

        visitor.VisitBranch(in storCtx0, branchNode);
        visitor.VisitLeaf(in storCtx1, leafNode);
        visitor.VisitLeaf(in storCtx1, leafNode);

        AccountStruct nextAccount = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in nextAccount);

        ValueHash256 owner = Keccak.Compute("owner").ValueHash256;
        TopContractEntry? result = visitor.GetResult(owner, targetRoot);

        Assert.That(result, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            // Convention 3: MaxDepth = raw(1) + 1 = 2
            Assert.That(result.Value.MaxDepth, Is.EqualTo(2), "C3: MaxDepth = raw + 1");
            // Convention 4: TotalNodes = physical(3) + value(2) = 5
            Assert.That(result.Value.TotalNodes, Is.EqualTo(5), "C4: TotalNodes double-counts leaves");
            Assert.That(result.Value.ValueNodes, Is.EqualTo(2));
            Assert.That(result.Value.Owner, Is.EqualTo(owner));
            Assert.That(result.Value.StorageRoot, Is.EqualTo(targetRoot));
            Assert.That(result.Value.Levels, Has.Length.EqualTo(VisitorCounters.MaxTrackedDepth));
        }
    }

    [Test]
    public void ShouldVisit_ReturnsFalse_AfterTargetCompleted()
    {
        ValueHash256 targetRoot = Keccak.Compute("target").ValueHash256;

        using SingleContractVisitor visitor = new(
            LimboLogs.Instance, targetStorageRoot: targetRoot, ct: CancellationToken.None);

        TrieNode node = new(NodeType.Leaf, [0xc0]);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        AccountStruct target = new(0, 0, targetRoot, Keccak.Zero.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in target);

        AccountStruct next = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in next);

        Assert.That(visitor.ShouldVisit(in accountCtx, in hash), Is.False,
            "ShouldVisit must return false after target completed");
    }

    [Test]
    public void ShouldVisit_SkipsNonTargetStorage()
    {
        ValueHash256 targetRoot = Keccak.Compute("target").ValueHash256;

        using SingleContractVisitor visitor = new(
            LimboLogs.Instance, targetStorageRoot: targetRoot, ct: CancellationToken.None);

        ValueHash256 hash = default;
        StateCompositionContext storageCtx = new(default, level: 1, isStorage: true, branchChildIndex: null);

        Assert.That(visitor.ShouldVisit(in storageCtx, in hash), Is.False,
            "Storage visits must be skipped before target is found");
    }

    [Test]
    public void ShouldVisit_ReturnsFalse_WhenCancelled()
    {
        using CancellationTokenSource cts = new();
        cts.Cancel();

        using SingleContractVisitor visitor = new(
            LimboLogs.Instance, targetStorageRoot: default, ct: cts.Token);

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        Assert.That(visitor.ShouldVisit(in ctx, in hash), Is.False);
    }
}
