// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Intrinsics.X86;
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

// Bench-only: FlatDb.FusedAccountTrieUpdate.
public partial class FlatWorldStateScopeProviderTests
{
    private sealed record FusedBatch(List<(Address Address, Account? Account)> Accounts, List<(Address Address, UInt256 Slot, UInt256 Value)> Slots);

    [Test]
    public void FusedAccountTrieUpdate_CommitsWhatTheTwoPassesCommit([Values(1, 128)] int minAccounts, [Range(0, 3)] int seed)
    {
        if (!Avx2.IsSupported || Core.Cpu.RuntimeInformation.IsSingleProcessor) Assert.Ignore("The fused pass needs AVX2 and more than one core.");

        using TestContext on = new(config: new FlatDbConfig { FusedAccountTrieUpdate = true, FusedAccountTrieMinAccounts = minAccounts });
        using TestContext off = new(config: new FlatDbConfig { FusedAccountTrieUpdate = false });
        Random rng = new(seed);
        List<Address> live = [];
        int nextAddress = 1;
        long fusedLoops = 0;

        for (int block = 1; block <= 8; block++)
        {
            List<FusedBatch> batches = RandomFusedBlock(rng, live, ref nextAddress);
            BlockDiag diagOn = RunFusedBlock(on.Scope, (ulong)block, batches);
            BlockDiag diagOff = RunFusedBlock(off.Scope, (ulong)block, batches);
            HashSet<string> nodesOn = CommittedNodesWithRlp(on.LastCommittedSnapshot!);
            HashSet<string> nodesOff = CommittedNodesWithRlp(off.LastCommittedSnapshot!);

            string label = $"seed {seed}, block {block}, batches {string.Join("+", batches.Select(static batch => batch.Accounts.Count))}";
            using (Assert.EnterMultipleScope())
            {
                Assert.That(on.Scope.RootHash, Is.EqualTo(off.Scope.RootHash), $"{label}: root");
                Assert.That(nodesOn.SetEquals(nodesOff), Is.True, $"{label}: committed nodes ({nodesOn.Count} vs {nodesOff.Count})");
                // Each batch either ran the fused pass or fell back.
                Assert.That(diagOn.Get(BlockDiag.C.FusedLoops) + diagOn.Get(BlockDiag.C.FusedFallback), Is.EqualTo(batches.Count), $"{label}: fused + fallback");
                Assert.That(diagOn.Get(BlockDiag.C.FusedMain) + diagOn.Get(BlockDiag.C.FusedHelper), Is.GreaterThanOrEqualTo(diagOn.Get(BlockDiag.C.FusedLoops)), $"{label}: jobs");
                Assert.That(diagOff.Get(BlockDiag.C.FusedLoops) + diagOff.Get(BlockDiag.C.FusedFallback), Is.Zero, $"{label}: off");
            }

            fusedLoops += diagOn.Get(BlockDiag.C.FusedLoops);
        }

        Assert.That(fusedLoops, Is.GreaterThan(0), "the fused pass ran");
    }

    /// <summary>Writes a block's batches as block processing does, storage first, then hashes and commits.</summary>
    private static BlockDiag RunFusedBlock(FlatWorldStateScope scope, ulong number, List<FusedBatch> batches)
    {
        BlockDiag diag = new() { BlockNumber = number };
        BlockDiag? outer = BlockDiag.Current;
        BlockDiag.Current = diag;
        try
        {
            foreach (FusedBatch batch in batches)
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(batch.Accounts.Count);
                foreach (IGrouping<Address, (Address Address, UInt256 Slot, UInt256 Value)> contract in batch.Slots.GroupBy(static slot => slot.Address))
                {
                    using IWorldStateScopeProvider.IStorageWriteBatch storage = writeBatch.CreateStorageWriteBatch(contract.Key, contract.Count());
                    foreach ((_, UInt256 slot, UInt256 value) in contract) storage.Set(slot, value);
                }

                foreach ((Address address, Account? account) in batch.Accounts) writeBatch.Set(address, account);
            }

            scope.UpdateRootHash();
            scope.Commit(number);
        }
        finally
        {
            BlockDiag.Current = outer;
        }

        return diag;
    }

    /// <summary>One to two batches: creates, updates, deletes, deletes of absent accounts and storage writes, from a
    /// handful of accounts to a few hundred, so blocks fall on both sides of the minimum.</summary>
    private static List<FusedBatch> RandomFusedBlock(Random rng, List<Address> live, ref int nextAddress)
    {
        List<FusedBatch> batches = [];
        int batchCount = rng.Next(4) == 0 ? 2 : 1;
        for (int b = 0; b < batchCount; b++)
        {
            int size = b == 0 ? (rng.Next(3) == 0 ? rng.Next(0, 20) : rng.Next(100, 500)) : rng.Next(1, 40);
            Dictionary<Address, Account?> accounts = [];
            List<(Address, UInt256, UInt256)> slots = [];
            for (int i = 0; i < size; i++)
            {
                int op = rng.Next(100);
                if (live.Count > 0 && op < 40)
                {
                    accounts[live[rng.Next(live.Count)]] = new Account((ulong)rng.Next(1, 100), (UInt256)rng.Next(1, int.MaxValue));
                }
                else if (live.Count > 0 && op < 50)
                {
                    accounts[live[rng.Next(live.Count)]] = null;
                }
                else if (op < 55)
                {
                    accounts[Address.FromNumber((UInt256)(1_000_000 + nextAddress++))] = null;
                }
                else
                {
                    accounts[Address.FromNumber((UInt256)nextAddress++)] = new Account((ulong)rng.Next(0, 100), (UInt256)rng.Next(1, int.MaxValue));
                }
            }

            // Storage on a few accounts that live on past this batch; the drain then writes their storage roots.
            foreach (Address contract in live.Where(address => !accounts.TryGetValue(address, out Account? account) || account is not null).Take(rng.Next(0, 4)))
            {
                for (int s = rng.Next(1, 4); s > 0; s--) slots.Add((contract, (UInt256)rng.Next(0, 16), (UInt256)rng.Next(0, 3)));
            }

            foreach ((Address address, Account? account) in accounts)
            {
                if (account is null) live.Remove(address);
                else if (!live.Contains(address)) live.Add(address);
            }

            batches.Add(new FusedBatch([.. accounts.Select(static entry => (entry.Key, entry.Value))], slots));
        }

        return batches;
    }

    private static HashSet<string> CommittedNodesWithRlp(Snapshot snapshot)
    {
        HashSet<string> nodes = [.. snapshot.StateNodes.Select(static node => $"state/{node.Key.Key}/{node.Value.Keccak}/{Convert.ToHexString(node.Value.FullRlp.AsSpan())}")];
        foreach (KeyValuePair<HashedKey<(Hash256, TreePath)>, TrieNode> node in snapshot.StorageNodes)
        {
            nodes.Add($"{node.Key.Key.Item1}/{node.Key.Key.Item2}/{node.Value.Keccak}/{Convert.ToHexString(node.Value.FullRlp.AsSpan())}");
        }

        return nodes;
    }
}
