// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using NUnit.Framework;

namespace Nethermind.Crypto.Gpu.Test;

/// <summary>
/// End-to-end: the BAL state-root calculator's batched path, hashing through the threshold router over the real GPU,
/// must produce the same root as a direct trie apply and as the recursive path. Skipped when no GPU is present.
/// </summary>
[TestFixture]
public class GpuBalStateRootIntegrationTests
{
    private static ITrieStore NewStore() => TestTrieStoreFactory.Build(new MemDb(), LimboLogs.Instance);

    private static byte[] ValueBytes(UInt256 value) =>
        value.IsZero ? [] : value.ToBigEndian().WithoutLeadingZeros().ToArray();

    [Test]
    public void BatchedComputeRoot_OverGpu_MatchesDirectApplyAndRecursive()
    {
        if (!GpuKeccakBatchHasher.TryCreate(out GpuKeccakBatchHasher? gpu) || gpu is null)
        {
            Assert.Ignore("no GPU");
        }
        using GpuKeccakBatchHasher g = gpu!;
        // minBatch 1 forces even the tiny per-trie batches onto the GPU so this exercises the real device path.
        IKeccakBatchHasher router = new ThresholdKeccakBatchHasher(g, new ParallelKeccakBatchHasher(), minBatch: 1, LimboLogs.Instance);

        Address contract = TestItem.AddressA; // pre-existing contract with storage, gets a new slot
        Address eoa = TestItem.AddressB;      // pre-existing account, balance bump
        Address created = TestItem.AddressC;  // new account created in-block

        (UInt256 slot, UInt256 value)[] preStorage = [((UInt256)1, (UInt256)11), ((UInt256)2, (UInt256)22)];
        (UInt256 slot, UInt256 value)[] postStorageWrites = [((UInt256)3, (UInt256)33)]; // add a third slot

        // ---- shared committed pre-state, and the expected post root by direct apply (separate stores) ----
        ITrieStore expectedStore = NewStore();
        (BlockHeader parent, Hash256 expectedRoot) = BuildPreAndExpected(expectedStore, contract, eoa, created, preStorage, postStorageWrites);

        ITrieStore calcStore = NewStore();
        BuildPreAndExpected(calcStore, contract, eoa, created, preStorage, postStorageWrites); // seed identical pre-state

        // ---- BAL matching the direct apply ----
        ReadOnlyBlockAccessList bal = Build.A.BlockAccessList
            .WithAccountChanges(
                Build.An.AccountChanges
                    .WithAddress(contract)
                    .WithStorageChanges((UInt256)3, new StorageChange(0, (UInt256)33))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(eoa)
                    .WithBalanceChanges(new BalanceChange(0, (UInt256)5000))
                    .TestObject,
                Build.An.AccountChanges
                    .WithAddress(created)
                    .WithBalanceChanges(new BalanceChange(0, (UInt256)9000))
                    .WithNonceChanges(new NonceChange(0, 1))
                    .TestObject)
            .TestObject;

        BalPostStateDelta delta = BalPostStateDelta.Reduce(bal);
        BalStateRootCalculator calculator = new(calcStore, LimboLogs.Instance);

        Hash256 gpuRoot = calculator.ComputeRoot(parent, delta, router);
        Hash256 recursiveRoot = calculator.ComputeRoot(parent, delta);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gpuRoot, Is.EqualTo(expectedRoot), "GPU-batched root must equal the direct-apply root");
            Assert.That(gpuRoot, Is.EqualTo(recursiveRoot), "GPU-batched root must equal the recursive-path root");
        }
    }

    // Builds the committed pre-state over the store and returns the parent header plus the expected post root
    // (direct apply of the same changes the BAL encodes).
    private static (BlockHeader parent, Hash256 expectedRoot) BuildPreAndExpected(
        ITrieStore store, Address contract, Address eoa, Address created,
        (UInt256 slot, UInt256 value)[] preStorage, (UInt256 slot, UInt256 value)[] postStorageWrites)
    {
        // Pre-state: a contract with storage + code, and an EOA.
        Hash256 preStorageRoot = BuildStorage(store, contract, preStorage);
        Account contractPre = new Account(1, 100).WithChangedStorageRoot(preStorageRoot).WithChangedCodeHash(TestItem.KeccakA);
        Account eoaPre = new(2, 200);

        StateTree preTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        preTree.Set(contract, contractPre);
        preTree.Set(eoa, eoaPre);
        preTree.Commit();
        Hash256 preRoot = preTree.RootHash;

        // Expected post-state by direct apply.
        Hash256 postStorageRoot = ApplyStorage(store, contract, preStorageRoot, postStorageWrites);
        StateTree postTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        postTree.SetRootHash(preRoot, true);
        postTree.Set(contract, contractPre.WithChangedStorageRoot(postStorageRoot));
        postTree.Set(eoa, new Account(2, 5000)); // balance bump, nonce unchanged
        postTree.Set(created, new Account(1, 9000)); // new account
        postTree.UpdateRootHash(canBeParallel: false);
        Hash256 expectedRoot = postTree.RootHash;

        BlockHeader parent = Build.A.BlockHeader.WithStateRoot(preRoot).TestObject;
        return (parent, expectedRoot);
    }

    private static Hash256 BuildStorage(ITrieStore store, Address address, (UInt256 slot, UInt256 value)[] slots)
    {
        StorageTree tree = new(store.GetTrieStore(address), LimboLogs.Instance);
        foreach ((UInt256 slot, UInt256 value) in slots) tree.Set(slot, ValueBytes(value));
        tree.Commit();
        return tree.RootHash;
    }

    private static Hash256 ApplyStorage(ITrieStore store, Address address, Hash256 preRoot, (UInt256 slot, UInt256 value)[] slots)
    {
        StorageTree tree = new(store.GetTrieStore(address), preRoot, LimboLogs.Instance);
        foreach ((UInt256 slot, UInt256 value) in slots) tree.Set(slot, ValueBytes(value));
        tree.UpdateRootHash(canBeParallel: false);
        return tree.RootHash;
    }
}
