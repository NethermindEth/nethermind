// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

[TestFixture]
public class FlatWorldStateScopeDeferredRootTests
{
    private static readonly Address AddrA = TestItem.AddressA;
    private static readonly Address AddrB = TestItem.AddressB;
    private static readonly Address AddrC = TestItem.AddressC;

    private ResourcePool _pool = null!;

    [SetUp]
    public void SetUp() => _pool = new ResourcePool(new FlatDbConfig { CompactSize = 2 });

    // The differential gate for deferred roots: the same three blocks — with overlapping account writes so the
    // window collapse (last write per account wins) is exercised, plus a storage write so per-block storage
    // merkleization is covered — must produce the same boundary root whether roots are computed per block or
    // deferred to the last block. Interior snapshots must carry the known (header) roots in their StateIds.
    [Test]
    public void CommitDeferred_ThreeBlockWindow_BoundaryRootMatchesPerBlockProcessing()
    {
        Hash256[] referenceRoots = RunBlocks(BuildScope(out _), deferInterior: false);

        List<StateId> snapshotIds = [];
        FlatWorldStateScope deferredScope = BuildScope(out IFlatCommitTarget commitTarget, snapshotIds);
        Hash256[] deferredRoots = RunBlocks(deferredScope, deferInterior: true, knownInteriorRoots: referenceRoots);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(deferredRoots[2], Is.EqualTo(referenceRoots[2]),
                "the boundary block must compute the same root as per-block processing");
            Assert.That(snapshotIds, Has.Count.EqualTo(3), "every block must still produce a snapshot");
            Assert.That(snapshotIds[0], Is.EqualTo(new StateId(101, referenceRoots[0])),
                "the first interior snapshot must be committed under its known header root");
            Assert.That(snapshotIds[1], Is.EqualTo(new StateId(102, referenceRoots[1])),
                "the second interior snapshot must be committed under its known header root");
            Assert.That(snapshotIds[2], Is.EqualTo(new StateId(103, referenceRoots[2])),
                "the boundary snapshot must carry the recomputed root");
        }
    }

    [Test]
    public void UpdateRootHash_DuringDeferredBlock_KeepsThePreviousBoundaryRoot()
    {
        FlatWorldStateScope scope = BuildScope(out _);

        Assert.That(scope.BeginDeferredRootBlock(), Is.True, "precondition: a writable trie-backed scope can defer");
        WriteAccounts(scope, (AddrA, new Account(1, 1)));
        scope.UpdateRootHash();

        Assert.That(scope.RootHash, Is.EqualTo(Keccak.EmptyTreeHash),
            "a deferred block must not recompute the root; the trie stays at the previous boundary");
    }

    [Test]
    public void Commit_AfterBeginDeferredRootBlock_Throws()
    {
        FlatWorldStateScope scope = BuildScope(out _);
        scope.BeginDeferredRootBlock();

        Assert.That(() => scope.Commit(101), Throws.InvalidOperationException,
            "a deferred block committed through the normal path would compute a root missing its own changes");
    }

    [Test]
    public void CommitDeferred_WithoutBegin_Throws()
    {
        FlatWorldStateScope scope = BuildScope(out _);

        Assert.That(() => scope.CommitDeferred(101, TestItem.KeccakA), Throws.InvalidOperationException,
            "a deferred commit without a deferred block would silently skip the block's trie updates");
    }

    [Test]
    public void BeginDeferredRootBlock_OnReadOnlyScope_ReturnsFalse()
    {
        FlatWorldStateScope scope = BuildScope(out _, isReadOnly: true);

        Assert.That(scope.BeginDeferredRootBlock(), Is.False, "read-only scopes never add snapshots so deferral is meaningless");
    }

    [Test]
    public void BeginDeferredRootBlock_WithVerifyWithTrie_ReturnsFalse()
    {
        FlatWorldStateScope scope = BuildScope(out _, verifyWithTrie: true);

        Assert.That(scope.BeginDeferredRootBlock(), Is.False,
            "trie verification reads the trie per account and would report false mismatches against a deferred trie");
    }

    // Runs blocks 101 (A=1, B=1, slot on A), 102 (A=2, C=1) and 103 (B=2) and returns the per-block roots.
    // With deferInterior, blocks 101 and 102 are deferred under the supplied known roots and only 103 computes.
    private static Hash256[] RunBlocks(FlatWorldStateScope scope, bool deferInterior, Hash256[]? knownInteriorRoots = null)
    {
        Hash256[] roots = new Hash256[3];

        (Address Address, Account Account)[][] blocks =
        [
            [(AddrA, new Account(1, 100)), (AddrB, new Account(1, 200))],
            [(AddrA, new Account(2, 111)), (AddrC, new Account(1, 300))],
            [(AddrB, new Account(2, 222))],
        ];

        for (int i = 0; i < blocks.Length; i++)
        {
            ulong blockNumber = 101ul + (ulong)i;
            bool defer = deferInterior && i < blocks.Length - 1;
            if (defer)
                Assert.That(scope.BeginDeferredRootBlock(), Is.True, $"precondition: block {blockNumber} must enter deferral");

            WriteAccounts(scope, includeStorage: i == 0, blocks[i]);

            scope.UpdateRootHash();
            if (defer)
            {
                scope.CommitDeferred(blockNumber, knownInteriorRoots![i]);
                roots[i] = knownInteriorRoots[i];
            }
            else
            {
                scope.Commit(blockNumber);
                roots[i] = scope.RootHash;
            }
        }

        return roots;
    }

    private static void WriteAccounts(FlatWorldStateScope scope, params (Address Address, Account Account)[] accounts) =>
        WriteAccounts(scope, includeStorage: false, accounts);

    private static void WriteAccounts(FlatWorldStateScope scope, bool includeStorage, (Address Address, Account Account)[] accounts)
    {
        using IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope.StartWriteBatch(accounts.Length);
        foreach ((Address address, Account account) in accounts)
        {
            batch.Set(address, account);
        }

        if (includeStorage)
        {
            UInt256 slot = (UInt256)7;
            using IWorldStateScopeProvider.IStorageWriteBatch storageBatch = batch.CreateStorageWriteBatch(accounts[0].Address, 1);
            storageBatch.Set(in slot, [0x12, 0x34]);
        }
    }

    private FlatWorldStateScope BuildScope(out IFlatCommitTarget commitTarget, List<StateId>? snapshotIds = null, bool isReadOnly = false, bool verifyWithTrie = false)
    {
        IPersistence.IPersistenceReader reader = Substitute.For<IPersistence.IPersistenceReader>();
        ReadOnlySnapshotBundle readOnlyBundle = new(
            FlatTestHelpers.SnapshotList(FlatTestHelpers.MakeSnapshot(_pool)),
            reader,
            recordDetailedMetrics: false);
        SnapshotBundle bundle = new(readOnlyBundle, Substitute.For<ITrieNodeCache>(), _pool, ResourcePool.Usage.MainBlockProcessing);

        commitTarget = Substitute.For<IFlatCommitTarget>();
        if (snapshotIds is not null)
        {
            List<StateId> ids = snapshotIds;
            commitTarget
                .When(static t => t.AddSnapshot(Arg.Any<Snapshot>(), Arg.Any<TransientResource>()))
                .Do(call => ids.Add(call.Arg<Snapshot>().To));
        }

        return new FlatWorldStateScope(
            new StateId(100, Keccak.EmptyTreeHash),
            bundle,
            new TrieStoreScopeProvider.KeyValueWithBatchingBackedCodeDb(new MemDb()),
            commitTarget,
            new FlatDbConfig { CompactSize = 2, VerifyWithTrie = verifyWithTrie },
            new NoopTrieWarmer(),
            LimboLogs.Instance,
            isReadOnly: isReadOnly);
    }
}
