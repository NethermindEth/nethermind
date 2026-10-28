// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable disable

using System;
using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync
{
    [TestFixture]
    public class RecreateStateFromStorageRangesTests
    {
        private TestRawTrieStore _store;
        private StateTree _inputStateTree;
        private StorageTree _inputStorageTree;
        private Hash256 _storage;

        [OneTimeSetUp]
        public void Setup()
        {
            _store = new TestRawTrieStore(new MemDb());
            (_inputStateTree, _inputStorageTree, _storage) = TestItem.Tree.GetTrees(_store);
        }

        [OneTimeTearDown]
        public void TearDown() => ((IDisposable)_store)?.Dispose();

        private ContainerBuilder CreateContainerBuilder() =>
            new ContainerBuilder()
                .AddModule(new TestSynchronizerModule(new TestSyncConfig()))
                .AddKeyedSingleton<IDb>(DbNames.State, (_) => (IDb)new TestMemDb())
                .AddSingleton<ISnapTestHelper, PatriciaSnapTestHelper>()
                ;

        private IContainer CreateContainer() =>
            CreateContainerBuilder().Build();

        [Test]
        public void RecreateStorageStateFromOneRangeWithNonExistenceProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            using IContainer container = CreateContainer();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            StorageRange storageRange = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            AddRangeResult result = snapProvider.AddStorageRangeForAccount(storageRange, 0, TestItem.Tree.SlotsWithPaths, new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromOneRangeWithExistenceProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { TestItem.Tree.SlotsWithPaths[0].Path, TestItem.Tree.SlotsWithPaths[5].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            using IContainer container = CreateContainer();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            StorageRange storageRange = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            AddRangeResult result = snapProvider.AddStorageRangeForAccount(storageRange, 0, TestItem.Tree.SlotsWithPaths, new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateStorageStateFromOneRangeWithoutProof()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            using IContainer container = CreateContainer();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            StorageRange storageRange = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[0].Path);
            AddRangeResult result = snapProvider.AddStorageRangeForAccount(storageRange, 0, TestItem.Tree.SlotsWithPaths);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void RecreateAccountStateFromMultipleRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            using IContainer container = CreateContainer();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            StorageRange storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            AddRangeResult result1 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[0..2], new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[2].Path);
            AddRangeResult result2 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[2..4], new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[4].Path);
            AddRangeResult result3 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[4..6], new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void MissingAccountFromRange()
        {
            Hash256 rootHash = _inputStorageTree!.RootHash;   // "..."

            // output state
            using IContainer container = CreateContainer();
            SnapProvider snapProvider = container.Resolve<SnapProvider>();

            AccountProofCollector accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, new ValueHash256[] { Keccak.Zero, TestItem.Tree.SlotsWithPaths[1].Path });
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            AccountProof proof = accountProofCollector.BuildResult();

            StorageRange storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, Keccak.Zero);
            AddRangeResult result1 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[0..2], new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[2].Path, TestItem.Tree.SlotsWithPaths[3].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[2].Path);
            AddRangeResult result2 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[3..4], new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            accountProofCollector = new(TestItem.Tree.AccountAddress0.Bytes, [TestItem.Tree.SlotsWithPaths[4].Path, TestItem.Tree.SlotsWithPaths[5].Path]);
            _inputStateTree!.Accept(accountProofCollector, _inputStateTree.RootHash);
            proof = accountProofCollector.BuildResult();

            storageRangeRequest = PrepareStorageRequest(TestItem.Tree.AccountAddress0, rootHash, TestItem.Tree.SlotsWithPaths[4].Path);
            AddRangeResult result3 = snapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, TestItem.Tree.SlotsWithPaths[4..6], new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proof.StorageProofs[0].Proof.Length + proof.StorageProofs[1].Proof.Length, proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof))));

            Assert.That(result1, Is.EqualTo(AddRangeResult.OK));
            Assert.That(result2, Is.EqualTo(AddRangeResult.DifferentRootHash));
            Assert.That(result3, Is.EqualTo(AddRangeResult.OK));
        }

        [Test]
        public void AddStorageRange_WhereProofIsTheSameAsAllKey_ShouldStillStore()
        {
            Hash256 account = TestItem.KeccakA;
            using IContainer container = CreateContainerBuilder()
                .Build();
            ISnapTestHelper helper = container.Resolve<ISnapTestHelper>();
            ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();

            PathWithAccount pathWithAccount = new(account, new Account(1, 1, new Hash256("0xeb8594ba5b3314111518b584bbd3801fb3aed5970bd8b47fd9ff744505fe101c"), TestItem.KeccakA));
            (AddRangeResult result, bool moreChildrenToRight, Hash256 _, bool rootFinished) = SnapProviderHelper.AddStorageRange(
                factory,
                pathWithAccount,
                [
                    new PathWithStorageSlot(new ValueHash256("0x290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e563"), Bytes.FromHexString("94654f75e491acf8c380d2a6906e67e2e56813665e")),
                ],
                Keccak.Zero,
                null,
                proofs: new ByteArrayListAdapter(new ArrayPoolList<byte[]>(2)
                {
                    Bytes.FromHexString("f838a120290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e5639594654f75e491acf8c380d2a6906e67e2e56813665e"),
                    Bytes.FromHexString("f838a120290decd9548b62a8d60345a988386fc84ba6bc95484008f6362f93160ef3e5639594654f75e491acf8c380d2a6906e67e2e56813665e"),
                }));

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(moreChildrenToRight, Is.False);
            Assert.That(helper.TrieNodeWritesCount, Is.EqualTo(1));
        }

        [Test]
        public void AddStorageRange_EmptySlots_ReturnsEmptySlots()
        {
            Hash256 account = TestItem.KeccakA;
            using IContainer container = CreateContainerBuilder()
                .Build();

            ISnapTestHelper helper = container.Resolve<ISnapTestHelper>();
            ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();

            PathWithAccount pathWithAccount = new(account, new Account(1, 1, new Hash256("0xeb8594ba5b3314111518b584bbd3801fb3aed5970bd8b47fd9ff744505fe101c"), TestItem.KeccakA));
            (AddRangeResult result, bool moreChildrenToRight, Hash256 _, bool rootFinished) = SnapProviderHelper.AddStorageRange(
                factory,
                pathWithAccount,
                Array.Empty<PathWithStorageSlot>(), // Empty slots list
                Keccak.Zero,
                null,
                proofs: null);

            Assert.That(result, Is.EqualTo(AddRangeResult.EmptyRange));
            Assert.That(helper.TrieNodeWritesCount, Is.EqualTo(0)); // No writes should happen
        }

        [Test]
        public void AddStorageRange_ZeroNibbleExtension_Rejected([Values] bool underBranch)
        {
            (Hash256 storageRoot, ArrayPoolList<byte[]> proofList) = BuildZeroNibbleExtensionProof(underBranch);

            PathWithAccount account = new(
                TestItem.KeccakA,
                new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot));

            PathWithStorageSlot[] slots = [new(ValueKeccak.Zero, [0x01])];

            using IContainer container = CreateContainerBuilder().Build();
            ISnapTrieFactory factory = container.Resolve<ISnapTrieFactory>();
            using ByteArrayListAdapter proofs = new(proofList);

            (AddRangeResult result, _, _, _) = SnapProviderHelper.AddStorageRange(
                factory, account, slots, Keccak.Zero, null,
                proofs);

            Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProofNode));
        }

        /// <summary>
        /// Builds a proof set containing a zero-nibble extension node, reached either as the proof root or,
        /// when <paramref name="underBranch"/> is set, as the left-boundary child of a branch root.
        /// </summary>
        private static (Hash256 rootHash, ArrayPoolList<byte[]> proofList) BuildZeroNibbleExtensionProof(bool underBranch)
        {
            byte[] extension = new byte[35];
            extension[0] = (byte)(Rlp.EmptyListByte + extension.Length - 1);
            extension[1] = 0x00; // hex-prefix: zero-nibble extension
            extension[2] = 0xa0; // bytes32 header
            extension[^1] = 0x01; // child hash, absent from the proof set and never resolved

            ArrayPoolList<byte[]> proofList = [with(2), extension];
            byte[] rootHash = Keccak.Compute(extension).BytesToArray();

            if (underBranch)
            {
                byte[] branch = BuildBranchWithFirstChild(rootHash);
                proofList.Add(branch);
                rootHash = Keccak.Compute(branch).BytesToArray();
            }

            return (new Hash256(rootHash), proofList);
        }

        /// <summary>
        /// A 17-item branch whose only child sits at nibble 0, placing it on the left boundary of a range starting at <see cref="Keccak.Zero"/>.
        /// </summary>
        private static byte[] BuildBranchWithFirstChild(byte[] childHash)
        {
            const int emptyItemCount = 16; // 15 empty children plus the empty value slot
            const int childOffset = 2;

            byte[] branch = new byte[childOffset + Hash256.Size + emptyItemCount];
            branch[0] = (byte)(Rlp.EmptyListByte + branch.Length - 1);
            branch[1] = 0xa0; // bytes32 header
            childHash.CopyTo(branch.AsSpan(childOffset));
            branch.AsSpan(childOffset + Hash256.Size).Fill(Rlp.EmptyByteArrayByte);

            return branch;
        }

        private static StorageRange PrepareStorageRequest(ValueHash256 accountPath, Hash256 storageRoot, ValueHash256 startingHash) =>
            new()
            {
                StartingHash = startingHash,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(accountPath, new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot)) }
            };
    }
}
