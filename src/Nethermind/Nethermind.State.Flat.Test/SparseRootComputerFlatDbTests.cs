// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Threading;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Tests that reproduce the EXPB mismatch by using the flat DB reader path
/// (ParentStateTrieNodeReader with ReadOnlySnapshotBundle) instead of
/// HalfPathTrieNodeReader. If these tests fail, it proves the flat DB reader
/// returns different data than HalfPath for the same trie.
/// </summary>
[TestFixture]
public class SparseRootComputerFlatDbTests
{
    [Test]
    public void FlatDbReader_SnapshotChain_FindsNodes()
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 2 });

        TreePath rootPath = TreePath.Empty;
        byte[] rootRlp = [0xc8, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80,
                          0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80];
        Hash256 rootHash = Keccak.Compute(rootRlp);
        TrieNode rootNode = new(NodeType.Unknown, rootHash, rootRlp);

        SnapshotContent snapshotContent = pool.GetSnapshotContent(ResourcePool.Usage.MainBlockProcessing);
        snapshotContent.StateNodes[new HashedKey<TreePath>(rootPath)] = rootNode;

        Snapshot snapshot = new(StateId.PreGenesis, new StateId(1, rootHash.ValueHash256),
            snapshotContent, pool, ResourcePool.Usage.MainBlockProcessing);
        SnapshotPooledList snapList = FlatTestHelpers.SnapshotList(snapshot);

        IPersistence.IPersistenceReader mockReader = Substitute.For<IPersistence.IPersistenceReader>();
        ReadOnlySnapshotBundle roBundle = new(snapList, mockReader, false, PersistedSnapshotStack.Empty());

        ITrieNodeCache noopCache = Substitute.For<ITrieNodeCache>();
        SnapshotBundle bundle = new(roBundle, noopCache, pool, ResourcePool.Usage.MainBlockProcessing);

        ParentStateTrieNodeReader proofReader = new(bundle);

        byte[] result = proofReader.LoadStateRlp(rootPath, rootHash);
        Assert.That(result, Is.EqualTo(rootRlp), "ParentStateTrieNodeReader must find node in snapshot chain");

        Hash256 resultHash = Keccak.Compute(result);
        Assert.That(resultHash, Is.EqualTo(rootHash), "Returned RLP must hash to the expected hash");
    }

    /// <summary>
    /// Reproduces the EXPB verification flow exactly:
    /// - Patricia tree commits via StateTrieStoreAdapter to SnapshotBundle
    /// - CollectAndApplySnapshot moves nodes to snapshot chain
    /// - ParentStateTrieNodeReader reads proofs from the same SnapshotBundle
    /// - SparseRootComputer computes root and compares
    /// Blocks 1-5 match in EXPB but blocks 6+ mismatch. This test should reproduce that.
    /// </summary>
    [TestCase(500, 15, 50)]
    [TestCase(1000, 20, 100)]
    [TestCase(200, 10, 30)]
    [TestCase(10000, 5, 400)]
    [TestCase(20000, 10, 500)]
    [TestCase(100000, 3, 500)]
    public void MultiBlock_SnapshotBundleReader_MatchesPatricia(int trieSize, int numBlocks, int changesPerBlock)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 32 });
        IPersistence.IPersistenceReader mockPersistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        ITrieNodeCache noopCache = Substitute.For<ITrieNodeCache>();

        SnapshotPooledList initialSnapshots = new(1);
        ReadOnlySnapshotBundle roBundle = new(initialSnapshots, mockPersistenceReader, false, PersistedSnapshotStack.Empty());
        SnapshotBundle snapshotBundle = new(roBundle, noopCache, pool, ResourcePool.Usage.MainBlockProcessing);

        ConcurrencyController concurrencyQuota = new(Environment.ProcessorCount);
        StateTrieStoreAdapter storeAdapter = new(snapshotBundle, concurrencyQuota);
        StateTree stateTree = new(storeAdapter, LimboLogs.Instance)
        {
            RootHash = Keccak.EmptyTreeHash
        };

        // Also build a reference Patricia tree in MemDb for comparison
        MemDb refDb = new();
        PatriciaTree refTree = new(new RawTrieStore(refDb).GetTrieStore(null), LimboLogs.Instance);

        Hash256[] keys = new Hash256[trieSize];
        for (int i = 0; i < trieSize; i++)
            keys[i] = Keccak.Compute(System.BitConverter.GetBytes(i));

        // Genesis: insert initial accounts into both trees
        for (int i = 0; i < trieSize; i++)
        {
            byte[] rlp = TestItem.GenerateIndexedAccountRlp(i);
            stateTree.Set(keys[i].Bytes, rlp);
            refTree.Set(keys[i].Bytes, rlp);
        }
        stateTree.UpdateRootHash();
        stateTree.Commit();
        refTree.UpdateRootHash();
        refTree.Commit();
        Hash256 prevRoot = stateTree.RootHash;
        Assert.That(prevRoot, Is.EqualTo(refTree.RootHash), "Genesis roots should match");

        // Move genesis nodes to snapshot
        StateId genesisStateId = new(0, prevRoot.ValueHash256);
        (Snapshot? genSnap, TransientResource? genRes) =
            snapshotBundle.CollectAndApplySnapshot(StateId.PreGenesis, genesisStateId);
        genRes?.Dispose();

        // Create sparse trie components
        SparseStateTrie sparseState = new();
        Random rng = new(42);

        for (int block = 1; block <= numBlocks; block++)
        {
            // Determine which accounts to update
            int startIdx = rng.Next(0, trieSize - changesPerBlock);
            Dictionary<ValueHash256, LeafUpdate> sparseUpdates = new(changesPerBlock);

            for (int i = startIdx; i < startIdx + changesPerBlock; i++)
            {
                byte[] newRlp = TestItem.GenerateIndexedAccountRlp(10000 + block * changesPerBlock + i);

                stateTree.Set(keys[i].Bytes, newRlp);
                refTree.Set(keys[i].Bytes, newRlp);
                sparseUpdates[keys[i]] = LeafUpdate.Changed(newRlp);
            }

            // Patricia: UpdateRootHash + Commit on the StateTree (SnapshotBundle-backed)
            stateTree.UpdateRootHash();
            Hash256 patriciaRoot = stateTree.RootHash;

            // Reference tree for sanity
            refTree.UpdateRootHash();
            refTree.Commit();
            Hash256 refRoot = refTree.RootHash;
            Assert.That(patriciaRoot, Is.EqualTo(refRoot), $"Block {block}: Patricia (SnapshotBundle) root should match ref tree");

            // Sparse: read proofs from ParentStateTrieNodeReader, update, compute root
            ParentStateTrieNodeReader proofReader = new(snapshotBundle);
            using SparseRootComputer computer = new(sparseState, proofReader, prevRoot);
            computer.SetAccountChanges(sparseUpdates);

            Hash256 sparseRoot = computer.ComputeStateRoot();

            TestContext.Out.WriteLine(
                $"Block {block}: prev={Shorten(prevRoot)}, patricia={Shorten(patriciaRoot)}, " +
                $"sparse={Shorten(sparseRoot)}, accounts={computer.AccountChangeCount}, " +
                $"proofNodes={computer.LastProofNodeCount}");

            Assert.That(sparseRoot, Is.EqualTo(patriciaRoot), $"Block {block}: sparse root must match Patricia " +
                $"({changesPerBlock}/{trieSize} changes, ParentStateTrieNodeReader path)");

            // Commit Patricia to snapshot chain (same as FlatWorldStateScope.Commit)
            stateTree.Commit();
            StateId newStateId = new((ulong)block, patriciaRoot.ValueHash256);
            (Snapshot? snap, TransientResource? res) =
                snapshotBundle.CollectAndApplySnapshot(
                    new StateId((ulong)(block - 1), prevRoot.ValueHash256), newStateId);
            res?.Dispose();

            // Reset stateTree root for next block
            stateTree.RootHash = patriciaRoot;

            prevRoot = patriciaRoot;
        }

        sparseState.Dispose();
        snapshotBundle.Dispose();
    }

    private static string Shorten(Hash256 h) => h.ToString()[..10] + "...";

    /// <summary>
    /// Same as MultiBlock_SnapshotBundleReader_MatchesPatricia but uses accounts with
    /// non-empty storageRoot and codeHash â€” closer to real Ethereum state where most
    /// touched accounts are contracts with code and storage.
    /// </summary>
    [TestCase(5000, 10, 350)]
    [TestCase(20000, 10, 400)]
    public void MultiBlock_WithStorageRootAndCode_MatchesPatricia(int trieSize, int numBlocks, int changesPerBlock)
    {
        ResourcePool pool = new(new FlatDbConfig { CompactSize = 32 });
        IPersistence.IPersistenceReader mockPersistenceReader = Substitute.For<IPersistence.IPersistenceReader>();
        ITrieNodeCache noopCache = Substitute.For<ITrieNodeCache>();

        SnapshotPooledList initialSnapshots = new(1);
        ReadOnlySnapshotBundle roBundle = new(initialSnapshots, mockPersistenceReader, false, PersistedSnapshotStack.Empty());
        SnapshotBundle snapshotBundle = new(roBundle, noopCache, pool, ResourcePool.Usage.MainBlockProcessing);

        ConcurrencyController concurrencyQuota = new(Environment.ProcessorCount);
        StateTrieStoreAdapter storeAdapter = new(snapshotBundle, concurrencyQuota);
        StateTree stateTree = new(storeAdapter, LimboLogs.Instance)
        {
            RootHash = Keccak.EmptyTreeHash
        };

        Nethermind.Serialization.Rlp.AccountDecoder decoder = new();

        // Build a mix of: half are simple EOAs, half are contracts with codeHash + storageRoot
        Hash256[] keys = new Hash256[trieSize];
        Account[] accounts = new Account[trieSize];
        for (int i = 0; i < trieSize; i++)
        {
            keys[i] = Keccak.Compute(System.BitConverter.GetBytes(i));
            if (i % 2 == 0)
            {
                accounts[i] = new Account((ulong)i, (Nethermind.Int256.UInt256)(i + 1000));
            }
            else
            {
                Hash256 fakeStorageRoot = Keccak.Compute(System.BitConverter.GetBytes(i * 2 + 1));
                Hash256 fakeCodeHash = Keccak.Compute(System.BitConverter.GetBytes(i * 3 + 7));
                accounts[i] = new Account((ulong)i, (Nethermind.Int256.UInt256)(i + 1000),
                    fakeStorageRoot, fakeCodeHash);
            }
            byte[] rlp = decoder.Encode(accounts[i]).Bytes;
            stateTree.Set(keys[i].Bytes, rlp);
        }
        stateTree.UpdateRootHash();
        stateTree.Commit();
        Hash256 prevRoot = stateTree.RootHash;

        StateId genesisStateId = new(0, prevRoot.ValueHash256);
        (Snapshot? genSnap, TransientResource? genRes) =
            snapshotBundle.CollectAndApplySnapshot(StateId.PreGenesis, genesisStateId);
        genRes?.Dispose();

        Random rng = new(42);
        for (int block = 1; block <= numBlocks; block++)
        {
            int startIdx = rng.Next(0, trieSize - changesPerBlock);
            Dictionary<ValueHash256, LeafUpdate> sparseUpdates = new(changesPerBlock);

            for (int i = startIdx; i < startIdx + changesPerBlock; i++)
            {
                // Update account â€” bump nonce, change storage root if contract
                Account newAccount;
                if (i % 2 == 0)
                {
                    newAccount = new Account((ulong)(i + block * 100), (Nethermind.Int256.UInt256)(i + 1000));
                }
                else
                {
                    Hash256 newStorageRoot = Keccak.Compute(System.BitConverter.GetBytes(i * 2 + 1 + block));
                    Hash256 codeHash = accounts[i].CodeHash;
                    newAccount = new Account((ulong)(i + block * 100), (Nethermind.Int256.UInt256)(i + 1000),
                        newStorageRoot, codeHash);
                }
                byte[] newRlp = decoder.Encode(newAccount).Bytes;
                stateTree.Set(keys[i].Bytes, newRlp);
                sparseUpdates[keys[i]] = LeafUpdate.Changed(newRlp);
            }

            stateTree.UpdateRootHash();
            Hash256 patriciaRoot = stateTree.RootHash;

            ParentStateTrieNodeReader proofReader = new(snapshotBundle);
            using SparseRootComputer computer = new(proofReader, prevRoot);
            computer.SetAccountChanges(sparseUpdates);
            Hash256 sparseRoot = computer.ComputeStateRoot();

            TestContext.Out.WriteLine(
                $"Block {block}: prev={Shorten(prevRoot)}, patricia={Shorten(patriciaRoot)}, " +
                $"sparse={Shorten(sparseRoot)}, accounts={computer.AccountChangeCount}, " +
                $"proofNodes={computer.LastProofNodeCount}");

            Assert.That(sparseRoot, Is.EqualTo(patriciaRoot), $"Block {block}: sparse root must match Patricia with contract accounts ({changesPerBlock}/{trieSize} changes)");

            stateTree.Commit();
            StateId newStateId = new((ulong)block, patriciaRoot.ValueHash256);
            (Snapshot? snap, TransientResource? res) =
                snapshotBundle.CollectAndApplySnapshot(
                    new StateId((ulong)(block - 1), prevRoot.ValueHash256), newStateId);
            res?.Dispose();
            stateTree.RootHash = patriciaRoot;
            prevRoot = patriciaRoot;
        }

        snapshotBundle.Dispose();
    }
}
