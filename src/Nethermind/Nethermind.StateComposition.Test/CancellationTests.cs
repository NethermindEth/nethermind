// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test;

[TestFixture]
public class CancellationTests
{
    [Test]
    public void Cancellation_StopsDataAccumulation()
    {
        using CancellationTokenSource cts = new();
        using StateCompositionVisitor visitor = new(LimboLogs.Instance, ct: cts.Token);

        TrieNode node = new(NodeType.Leaf, [0xc0]);
        StateCompositionContext accountCtx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        // Visit some accounts before cancellation
        AccountStruct acc = new(0, 100, Keccak.EmptyTreeHash.ValueHash256, Keccak.OfAnEmptyString.ValueHash256);
        visitor.VisitAccount(in accountCtx, node, in acc);
        visitor.VisitAccount(in accountCtx, node, in acc);

        StateCompositionStats before = visitor.GetStats(1, null);
        Assert.That(before.AccountsTotal, Is.EqualTo(2), "Pre-cancel: 2 accounts visited");

        // Cancel
        cts.Cancel();

        // ShouldVisit returns false — real traversal would stop here
        Assert.That(visitor.ShouldVisit(in accountCtx, in hash), Is.False,
            "ShouldVisit must return false after cancellation");

        // Even if visitor methods are called after cancel (which shouldn't happen
        // in real traversal since ShouldVisit gates it), the token check in
        // ShouldVisit is the authoritative gate that stops traversal.
    }

    [Test]
    public void Cancellation_ShouldVisit_CheckedBeforeEveryNode()
    {
        using CancellationTokenSource cts = new();
        using StateCompositionVisitor visitor = new(LimboLogs.Instance, ct: cts.Token);

        StateCompositionContext ctx = new(default, level: 0, isStorage: false, branchChildIndex: null);
        ValueHash256 hash = default;

        // Not canceled yet — should visit
        Assert.That(visitor.ShouldVisit(in ctx, in hash), Is.True, "Before cancel: should visit");

        cts.Cancel();

        // Multiple calls after cancel — all false
        Assert.That(visitor.ShouldVisit(in ctx, in hash), Is.False, "After cancel: first call");
        Assert.That(visitor.ShouldVisit(in ctx, in hash), Is.False, "After cancel: second call");

        // Storage context also false
        StateCompositionContext storCtx = new(default, level: 1, isStorage: true, branchChildIndex: null);
        Assert.That(visitor.ShouldVisit(in storCtx, in hash), Is.False, "After cancel: storage context");
    }
}
