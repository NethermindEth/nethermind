// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Visitors;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Visitors;

/// <summary>
/// Verifies CodeBytesTotal aggregation by codeHash:
/// - unique codeHashes sum their bytecode length;
/// - duplicate codeHashes (proxies/clones) contribute 0 on second observation;
/// - accounts without code (EOAs) are skipped.
/// </summary>
[TestFixture]
public class CodeBytesTests
{
    private static readonly TrieNode AccountLeaf = new(NodeType.Leaf, [0xc0]);
    private static readonly StateCompositionContext AccountCtx =
        new(default, level: 0, isStorage: false, branchChildIndex: null);

    private static readonly ValueHash256 EmptyCodeHash = Keccak.OfAnEmptyString.ValueHash256;

    [Test]
    public void CodeBytesTotal_UniqueByCodeHash_SumsBytecodeLengths()
    {
        // Three distinct codeHashes, sizes 100 / 200 / 50.
        // Two contracts share the 200-byte codeHash (proxy / clone pattern)
        // so the second observation must contribute 0 bytes.
        ValueHash256 hashA = Keccak.Compute("contractA").ValueHash256;
        ValueHash256 hashB = Keccak.Compute("contractB").ValueHash256;
        ValueHash256 hashC = Keccak.Compute("contractC").ValueHash256;

        Dictionary<ValueHash256, int> sizes = new()
        {
            [hashA] = 100,
            [hashB] = 200,
            [hashC] = 50,
        };

        int lookupCalls = 0;

        using StateCompositionVisitor visitor =
            new(LimboLogs.Instance, codeSizeLookup: Lookup);

        AccountStruct accA = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, hashA);
        AccountStruct accB = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, hashB);
        AccountStruct accBClone = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, hashB);
        AccountStruct accC = new(0, 0, Keccak.EmptyTreeHash.ValueHash256, hashC);

        visitor.VisitAccount(in AccountCtx, AccountLeaf, in accA);
        visitor.VisitAccount(in AccountCtx, AccountLeaf, in accB);
        visitor.VisitAccount(in AccountCtx, AccountLeaf, in accBClone);
        visitor.VisitAccount(in AccountCtx, AccountLeaf, in accC);

        StateCompositionStats stats = visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.CodeBytesTotal, Is.EqualTo(100 + 200 + 50),
                "unique codeHashes sum to 350; duplicate hashB must not double-count");
            Assert.That(lookupCalls, Is.EqualTo(3),
                "lookup must be deduplicated — 3 unique codeHashes ⇒ 3 lookups");
            Assert.That(stats.ContractsTotal, Is.EqualTo(4),
                "ContractsTotal still counts every contract (not deduped)");
        }

        return;

        int Lookup(ValueHash256 hash)
        {
            lookupCalls++;
            return sizes.TryGetValue(hash, out int size) ? size : 0;
        }
    }

    [Test]
    public void CodeBytesTotal_SkipsEmptyCodeHash_ForEoas()
    {
        bool lookupCalled = false;

        using StateCompositionVisitor visitor =
            new(LimboLogs.Instance, codeSizeLookup: Lookup);

        AccountStruct eoa = new(0, 100, Keccak.EmptyTreeHash.ValueHash256, EmptyCodeHash);
        visitor.VisitAccount(in AccountCtx, AccountLeaf, in eoa);
        visitor.VisitAccount(in AccountCtx, AccountLeaf, in eoa);

        StateCompositionStats stats = visitor.GetStats(1, null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stats.CodeBytesTotal, Is.Zero, "EOAs contribute no bytes");
            Assert.That(stats.ContractsTotal, Is.Zero, "EOAs are not contracts");
            Assert.That(lookupCalled, Is.False, "lookup must not fire for EOAs");
        }

        return;

        int Lookup(ValueHash256 _)
        {
            lookupCalled = true;
            return 999;
        }
    }

}
