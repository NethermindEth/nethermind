// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

// Bench-only: FlatDb.ShadowAccountSplit.
public partial class FlatWorldStateScopeProviderTests
{
    // Flushed with a balance change, then its new storage root drained in.
    private static readonly Address ShadowStorageAndBalance = Address.FromNumber(1001);
    // A contract whose storage does not change in block 2.
    private static readonly Address ShadowBalanceOnly = Address.FromNumber(1002);
    // Only its storage root changes in block 2, so the state flush never writes it.
    private static readonly Address ShadowStorageOnly = Address.FromNumber(1003);
    private static readonly Address ShadowDeleted = Address.FromNumber(1004);
    private static readonly Address ShadowCreated = Address.FromNumber(2001);

    private static Address ShadowEoa(int i) => Address.FromNumber((UInt256)(i + 1));

    [Test]
    public void ShadowAccountSplit_ReproducesTheBlockRoot([Values(12, 300)] int eoaCount, [Values] bool twoBatches)
    {
        using TestContext ctx = new(config: new FlatDbConfig { ShadowAccountSplit = true });
        FlatWorldStateScope scope = ctx.Scope;

        for (int block = 1; block <= 2; block++)
        {
            BlockDiag diag = RunShadowBlock(scope, (ulong)block, withDiag: true, () => WriteShadowBlock(scope, block, eoaCount, twoBatches), out bool kept)!;

            // Block 1 creates every account; the contracts are not settled, their storage roots change. Block 2 leaves
            // out the contract flushed before its storage root changed, the storage-only contract and the delete.
            int settled = block == 1 ? eoaCount + 1 : eoaCount + 2;
            int distinct = block == 1 ? eoaCount + 4 : eoaCount + 5;
            (long fullWall, long fullCpu) = diag.Get(BlockDiag.F.ShFull);
            (long p1Wall, long p1Cpu) = diag.Get(BlockDiag.F.ShP1);
            (long p2Wall, long p2Cpu) = diag.Get(BlockDiag.F.ShP2);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(kept, Is.True, $"block {block} kept");
                Assert.That(scope.KeepsShadowBatch, Is.False, $"block {block} released");
                Assert.That(diag.Get(BlockDiag.C.ShRan), Is.EqualTo(1), $"block {block} ran");
                Assert.That(diag.Get(BlockDiag.C.ShErr), Is.Zero, $"block {block} err");
                Assert.That(diag.Get(BlockDiag.C.ShOk), Is.EqualTo(1), $"block {block} ok");
                Assert.That(diag.Get(BlockDiag.C.ShXFirst), Is.EqualTo(block % 2 == 0 ? 1 : 0), $"block {block} x first");
                Assert.That(diag.Get(BlockDiag.C.ShSettled), Is.EqualTo(settled), $"block {block} settled");
                Assert.That(diag.Get(BlockDiag.C.ShSettled) + diag.Get(BlockDiag.C.ShRest), Is.EqualTo(distinct), $"block {block} settled + rest");
                if (!twoBatches || block == 1)
                {
                    Assert.That(diag.Get(BlockDiag.C.ShSettled) + diag.Get(BlockDiag.C.ShRest), Is.EqualTo(diag.Get(BlockDiag.C.TrieAccounts)), $"block {block} = trie accounts");
                }
                Assert.That(fullWall, Is.GreaterThan(0), $"block {block} full");
                Assert.That(p1Wall, Is.GreaterThan(0), $"block {block} p1");
                Assert.That(p2Wall, Is.GreaterThan(0), $"block {block} p2");
                Assert.That(Math.Min(fullCpu, Math.Min(p1Cpu, p2Cpu)), Is.GreaterThanOrEqualTo(0), $"block {block} cpu");
            }
        }
    }

    [Test]
    public void ShadowAccountSplit_KeepsAndRecordsNothing_UnlessOnWithADiag([Values] bool flagOn, [Values] bool withDiag)
    {
        using TestContext ctx = new(config: new FlatDbConfig { ShadowAccountSplit = flagOn });
        FlatWorldStateScope scope = ctx.Scope;
        bool expected = flagOn && withDiag;

        for (int block = 1; block <= 2; block++)
        {
            BlockDiag? diag = RunShadowBlock(scope, (ulong)block, withDiag, () => WriteShadowBlock(scope, block, 12, twoBatches: false), out bool kept);

            Assert.That(kept, Is.EqualTo(expected), $"block {block} kept");
            Assert.That(scope.KeepsShadowBatch, Is.False, $"block {block} released");
            if (diag is null) continue;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(diag.Get(BlockDiag.C.ShRan), Is.EqualTo(expected ? 1 : 0), $"block {block} ran");
                Assert.That(diag.Get(BlockDiag.C.ShOk), Is.EqualTo(expected ? 1 : 0), $"block {block} ok");
                if (!expected)
                {
                    foreach (BlockDiag.C count in new[] { BlockDiag.C.ShSettled, BlockDiag.C.ShRest, BlockDiag.C.ShXFirst, BlockDiag.C.ShErr })
                    {
                        Assert.That(diag.Get(count), Is.Zero, $"block {block} {count}");
                    }

                    foreach (BlockDiag.F section in new[] { BlockDiag.F.ShFull, BlockDiag.F.ShP1, BlockDiag.F.ShP2 })
                    {
                        Assert.That(diag.Get(section), Is.EqualTo((0L, 0L)), $"block {block} {section}");
                    }
                }
            }
        }
    }

    [Test]
    public void ShadowAccountSplit_LeavesTheCommittedStateAlone([Values] bool twoBatches)
    {
        const int eoaCount = 300;
        using TestContext on = new(config: new FlatDbConfig { ShadowAccountSplit = true });
        using TestContext off = new(config: new FlatDbConfig { ShadowAccountSplit = false });

        for (int block = 1; block <= 2; block++)
        {
            BlockDiag diag = RunShadowBlock(on.Scope, (ulong)block, withDiag: true, () => WriteShadowBlock(on.Scope, block, eoaCount, twoBatches), out _)!;
            HashSet<string> nodesOn = CommittedNodes(on.LastCommittedSnapshot!);
            RunShadowBlock(off.Scope, (ulong)block, withDiag: true, () => WriteShadowBlock(off.Scope, block, eoaCount, twoBatches), out _);
            HashSet<string> nodesOff = CommittedNodes(off.LastCommittedSnapshot!);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(diag.Get(BlockDiag.C.ShOk), Is.EqualTo(1), $"block {block} ok");
                Assert.That(on.Scope.RootHash, Is.EqualTo(off.Scope.RootHash), $"block {block} root");
                Assert.That(nodesOn, Is.Not.Empty, $"block {block} nodes");
                Assert.That(nodesOn, Is.EquivalentTo(nodesOff), $"block {block} nodes");
            }
        }
    }

    private static HashSet<string> CommittedNodes(Snapshot snapshot)
    {
        HashSet<string> nodes = [.. snapshot.StateNodes.Select(static node => $"state/{node.Key.Key}/{node.Value.Keccak}")];
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> node in snapshot.StorageNodes)
        {
            nodes.Add($"{node.Key.Key.Item1}/{node.Key.Key.Item2}/{node.Value.Keccak}");
        }

        return nodes;
    }

    /// <summary>Writes a block's batches, then hashes and commits the state as block processing does.</summary>
    private static BlockDiag? RunShadowBlock(FlatWorldStateScope scope, ulong number, bool withDiag, Action write, out bool kept)
    {
        BlockDiag? diag = withDiag ? new BlockDiag { BlockNumber = number } : null;
        BlockDiag? outer = BlockDiag.Current;
        BlockDiag.Current = diag;
        try
        {
            write();
            kept = scope.KeepsShadowBatch;
            scope.UpdateRootHash();
            scope.Commit(number);
        }
        finally
        {
            BlockDiag.Current = outer;
        }

        return diag;
    }

    /// <summary>Block 1 creates the accounts; block 2 changes them in every way the split has to tell apart.</summary>
    private static void WriteShadowBlock(FlatWorldStateScope scope, int block, int eoaCount, bool twoBatches)
    {
        if (block == 1)
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(eoaCount + 4);
            // Storage first, as block processing flushes it.
            foreach (Address contract in new[] { ShadowStorageAndBalance, ShadowBalanceOnly, ShadowStorageOnly })
            {
                using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(contract, 2);
                storage.Set((UInt256)1, (UInt256)0xA1);
                storage.Set((UInt256)2, (UInt256)0xA2);
            }

            for (int i = 0; i < eoaCount; i++) batch.Set(ShadowEoa(i), new Account((ulong)i, (UInt256)(1000 + i)));
            batch.Set(ShadowStorageAndBalance, new Account(1, 5));
            batch.Set(ShadowBalanceOnly, new Account(1, 6));
            batch.Set(ShadowStorageOnly, new Account(1, 7));
            batch.Set(ShadowDeleted, new Account(0, 9));
            return;
        }

        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(eoaCount + 5))
        {
            using (IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(ShadowStorageAndBalance, 1))
            {
                storage.Set((UInt256)1, (UInt256)0xB1);
            }

            using (IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(ShadowStorageOnly, 1))
            {
                storage.Set((UInt256)2, (UInt256)0xB2);
            }

            for (int i = 0; i < eoaCount; i++) batch.Set(ShadowEoa(i), WithAddedBalance(scope, ShadowEoa(i), 1));
            // Changed again within the flush: the last value is the one the trie gets.
            batch.Set(ShadowEoa(0), WithAddedBalance(scope, ShadowEoa(0), 2));
            // Still carries block 1's storage root here; the drain writes the new one.
            batch.Set(ShadowStorageAndBalance, WithAddedBalance(scope, ShadowStorageAndBalance, 1));
            batch.Set(ShadowBalanceOnly, WithAddedBalance(scope, ShadowBalanceOnly, 1));
            batch.Set(ShadowDeleted, null);
            batch.Set(ShadowCreated, new Account(0, 1));
        }

        if (!twoBatches) return;

        // A second root commit in the block changes a settled account and the storage-changed contract again.
        using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(2))
        {
            batch.Set(ShadowEoa(0), WithAddedBalance(scope, ShadowEoa(0), 3));
            batch.Set(ShadowStorageAndBalance, WithAddedBalance(scope, ShadowStorageAndBalance, 3));
        }
    }

    private static Account WithAddedBalance(FlatWorldStateScope scope, Address address, UInt256 amount)
    {
        Account account = scope.Get(address)!;
        return new Account(account.Nonce, account.Balance + amount, account.StorageRoot, account.CodeHash);
    }
}
