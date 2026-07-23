using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.SnapSync;
using Nethermind.Trie;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class PatriciaSnapServerTests
{
    internal interface IWriteBatch : IDisposable
    {
        void SetAccount(Address address, Account account);
        void SetAccount(Hash256 accountPath, Account account);
        void SetSlot(Hash256 storagePath, ValueHash256 slotKey, byte[] value, bool rlpEncode = true);
    }

    internal interface ISnapServerContext : IDisposable
    {
        ISnapStateServer Server { get; }
        SnapProvider SnapProvider { get; }
        Hash256 RootHash { get; }
        int PersistedNodeCount { get; }

        IWriteBatch BeginWriteBatch();
        Hash256 GetStorageRoot(Hash256 storagePath);
    }

    private class TrieSnapServerContext : ISnapServerContext
    {
        private readonly TestRawTrieStore _store;
        private readonly StateTree _tree;
        private readonly MemDb _clientStateDb;

        public ISnapStateServer Server { get; }
        public SnapProvider SnapProvider { get; }
        public Hash256 RootHash => _tree.RootHash;
        public int PersistedNodeCount => _clientStateDb.Keys.Count;

        internal TrieSnapServerContext(ILastNStateRootTracker? lastNStateRootTracker = null)
        {
            MemDb stateDbServer = new();
            _store = new TestRawTrieStore(stateDbServer);
            _tree = new StateTree(_store, LimboLogs.Instance);
            Server = new PatriciaSnapServer(_store.AsReadOnly(), LimboLogs.Instance, lastNStateRootTracker);

            _clientStateDb = new MemDb();
            using ProgressTracker progressTracker = new(_clientStateDb, new TestSyncConfig(), new StateSyncPivot(null!, new TestSyncConfig(), LimboLogs.Instance), LimboLogs.Instance);
            INodeStorage nodeStorage = new NodeStorage(_clientStateDb);
            SnapProvider = new SnapProvider(progressTracker, new MemDb(), new PatriciaSnapTrieFactory(nodeStorage, LimboLogs.Instance), LimboLogs.Instance);
        }

        public IWriteBatch BeginWriteBatch() => new WriteBatch(this);
        public Hash256 GetStorageRoot(Hash256 accountPath) => _tree.Get(accountPath)!.StorageRoot;
        public void Dispose() => ((IDisposable)_store).Dispose();

        private class WriteBatch(TrieSnapServerContext ctx) : IWriteBatch
        {
            private readonly List<(Hash256 Path, Account Account)> _pendingAccounts = [];
            private readonly Dictionary<Hash256, StorageTree> _storageTrees = [];

            public void SetAccount(Address address, Account account) =>
                _pendingAccounts.Add((address.ToAccountPath.ToCommitment(), account));

            public void SetAccount(Hash256 accountPath, Account account) =>
                _pendingAccounts.Add((accountPath, account));

            public void SetSlot(Hash256 storagePath, ValueHash256 slotKey, byte[] value, bool rlpEncode = true)
            {
                if (!_storageTrees.TryGetValue(storagePath, out StorageTree? st))
                {
                    st = new StorageTree(ctx._store.GetTrieStore(storagePath), LimboLogs.Instance);
                    _storageTrees[storagePath] = st;
                }
                st.Set(slotKey, value, rlpEncode);
            }

            public void Dispose()
            {
                Dictionary<Hash256, Hash256> storageRoots = [];
                foreach ((Hash256? path, StorageTree? st) in _storageTrees)
                {
                    st.Commit();
                    storageRoots[path] = st.RootHash;
                }

                foreach ((Hash256? path, Account? account) in _pendingAccounts)
                {
                    Account finalAccount = storageRoots.TryGetValue(path, out Hash256? root)
                        ? account.WithChangedStorageRoot(root)
                        : account;
                    ctx._tree.Set(path, finalAccount);
                }

                ctx._tree.Commit();
            }
        }
    }

    private ISnapServerContext CreateContext(ILastNStateRootTracker? lastNStateRootTracker = null) =>
        new TrieSnapServerContext(lastNStateRootTracker);

    private static void FillWithTestAccounts(ISnapServerContext context)
    {
        using IWriteBatch batch = context.BeginWriteBatch();
        foreach (PathWithAccount pwa in TestItem.Tree.AccountsWithPaths)
            batch.SetAccount(pwa.Path.ToCommitment(), pwa.Account);
    }

    private static void FillMultipleAccounts(ISnapServerContext context, int count)
    {
        using IWriteBatch batch = context.BeginWriteBatch();
        for (int i = 0; i < count; i++)
            batch.SetAccount(Keccak.Compute(i.ToBigEndianByteArray()), Build.An.Account.WithBalance((UInt256)i).TestObject);
    }

    private static Hash256 FillAccountWithDefaultStorage(ISnapServerContext context)
    {
        using (IWriteBatch batch = context.BeginWriteBatch())
        {
            for (int i = 0; i < 6; i++)
                batch.SetSlot(TestItem.Tree.AccountAddress0, TestItem.Tree.SlotsWithPaths[i].Path, TestItem.Tree.SlotsWithPaths[i].SlotRlpValue, rlpEncode: false);
            batch.SetAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).TestObject);
        }
        return context.GetStorageRoot(TestItem.Tree.AccountAddress0);
    }

    private static Hash256 FillAccountWithStorage(ISnapServerContext context, int slotCount)
    {
        using (IWriteBatch batch = context.BeginWriteBatch())
        {
            for (int i = 0; i < slotCount; i++)
            {
                Hash256 key = Keccak.Compute(i.ToBigEndianByteArray());
                batch.SetSlot(TestItem.Tree.AccountAddress0, key, key.BytesToArray(), rlpEncode: false);
            }
            batch.SetAccount(TestItem.Tree.AccountAddress0, Build.An.Account.WithBalance(1).TestObject);
        }
        return context.GetStorageRoot(TestItem.Tree.AccountAddress0);
    }

    [Test]
    public void TestGetAccountRange()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, Keccak.Zero,
            accounts.ToArray(), proofs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(context.PersistedNodeCount, Is.EqualTo(10));
        }
        accounts.Dispose();
        proofs.Dispose();
    }

    [Test]
    public void TestGetAccountRange_InvalidRange()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.MaxValue, Keccak.Zero, 4000, CancellationToken.None);

        Assert.That(accounts.Count, Is.EqualTo(0));
        accounts.Dispose();
        proofs.Dispose();
    }

    // Refresh re-fetches a single account via GetAccountRange and verifies its storage root against the
    // state root from the proof. A correct root must verify and propagate the real storage root; a wrong
    // root must be rejected.
    [TestCase(true, AddRangeResult.OK)]
    [TestCase(false, AddRangeResult.DifferentRootHash)]
    public void TestRefreshAccount(bool useCorrectRoot, AddRangeResult expectedResult)
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);
        Hash256 storageRoot = FillAccountWithDefaultStorage(context);

        ValueHash256 path = TestItem.Tree.AccountsWithPaths[0].Path;
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);
        using AccountsAndProofs response = new() { PathAndAccounts = accounts, Proofs = proofs };

        PathWithAccount stale = new(path, Build.An.Account.WithBalance(2).TestObject);
        using AccountsToRefreshRequest request = new()
        {
            RootHash = useCorrectRoot ? context.RootHash : TestItem.KeccakA,
            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1)
            {
                new() { PathAndAccount = stale, StorageStartingHash = ValueKeccak.Zero, StorageHashLimit = Keccak.MaxValue }
            }
        };

        AddRangeResult result = context.SnapProvider.RefreshAccounts(request, response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expectedResult));
            // On success the stale empty storage root must be replaced by the verified one.
            Assert.That(stale.Account.StorageRoot, Is.EqualTo(useCorrectRoot ? storageRoot : Keccak.EmptyTreeHash));
        }
    }

    // The range proof can omit the leaf node. Verification must still succeed by setting the account from the
    // returned accounts (not by hashing a proof node), then checking the reconstructed root.
    [Test]
    public void TestRefreshAccount_LeafMissingFromProof()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);
        Hash256 storageRoot = FillAccountWithDefaultStorage(context);

        ValueHash256 path = TestItem.Tree.AccountsWithPaths[0].Path;
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);

        // Drop the leaf nodes from the proof; the leaves are still present in the returned accounts.
        ArrayPoolList<byte[]> trimmedProofs = new(proofs.Count);
        for (int i = 0; i < proofs.Count; i++)
        {
            byte[] proof = proofs[i].ToArray();
            TrieNode node = new(NodeType.Unknown, proof);
            node.ResolveNode(null!, TreePath.Empty);
            if (!node.IsLeaf) trimmedProofs.Add(proof);
        }
        Assert.That(trimmedProofs.Count, Is.LessThan(proofs.Count), "test setup: expected at least one leaf in the proof");
        proofs.Dispose();

        using AccountsAndProofs response = new() { PathAndAccounts = accounts, Proofs = new ByteArrayListAdapter(trimmedProofs) };
        PathWithAccount stale = new(path, Build.An.Account.WithBalance(2).TestObject);
        using AccountsToRefreshRequest request = new()
        {
            RootHash = context.RootHash,
            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1)
            {
                new() { PathAndAccount = stale, StorageStartingHash = ValueKeccak.Zero, StorageHashLimit = Keccak.MaxValue }
            }
        };

        AddRangeResult result = context.SnapProvider.RefreshAccounts(request, response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            Assert.That(stale.Account.StorageRoot, Is.EqualTo(storageRoot));
        }
    }

    // A non-existent account is a terminal no-op only when its absence is *proven* by the verified range -
    // the storage root must be left untouched and the refresh must not retry.
    [Test]
    public void TestRefreshAccount_VerifiedNotFound()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);

        // A path immediately before an existing account: the account is absent, but the next account proves it.
        ValueHash256 path = TestItem.Tree.AccountsWithPaths[1].Path.DecrementPath();
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
            context.Server.GetAccountRanges(context.RootHash, path, path.IncrementPath(), 4000, CancellationToken.None);
        using AccountsAndProofs response = new() { PathAndAccounts = accounts, Proofs = proofs };

        PathWithAccount stale = new(path, Build.An.Account.WithBalance(2).TestObject);
        using AccountsToRefreshRequest request = new()
        {
            RootHash = context.RootHash,
            Paths = new ArrayPoolList<AccountWithStorageStartingHash>(1)
            {
                new() { PathAndAccount = stale, StorageStartingHash = ValueKeccak.Zero, StorageHashLimit = Keccak.MaxValue }
            }
        };

        AddRangeResult result = context.SnapProvider.RefreshAccounts(request, response);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
            // Absent account: nothing adopted, storage root unchanged.
            Assert.That(stale.Account.StorageRoot, Is.EqualTo(Keccak.EmptyTreeHash));
        }
    }

    [Test]
    public void TestGetTrieNode_Root()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList([
            new PathGroup()
            {
                Group = [[]]
            }
        ]);
        using IByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, default)!;

        Assert.That(result.Count, Is.EqualTo(1));
    }

    [Test]
    public void TestGetTrieNode_Storage_Root()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList([
            new PathGroup()
            {
                Group = [TestItem.Tree.AccountsWithPaths[0].Path.Bytes.ToArray(), []]
            }
        ]);
        using IByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, default)!;

        Assert.That(result.Count, Is.EqualTo(1));
    }

    [Test]
    public void TestGetTrieNodes_RespectsHardResponseByteLimit()
    {
        using ISnapServerContext context = CreateContext();
        FillMultipleAccounts(context, 1000);

        int requestCount = 5000;
        PathGroup[] groups = new PathGroup[requestCount];
        for (int i = 0; i < requestCount; i++)
            groups[i] = new PathGroup { Group = [[]] };

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList(groups);
        using IByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, default)!;

        Assert.That(result.Count, Is.LessThan(requestCount));
    }

    [Test]
    public void TestGetTrieNodes_RespectsRequestedResponseByteLimit()
    {
        using ISnapServerContext context = CreateContext();
        FillMultipleAccounts(context, 1000);

        int requestCount = 5000;
        PathGroup[] groups = new PathGroup[requestCount];
        for (int i = 0; i < requestCount; i++)
            groups[i] = new PathGroup { Group = [[]] };

        using RlpPathGroupList pathSet = PathGroup.EncodeToRlpPathGroupList(groups);
        using IByteArrayList result = context.Server.GetTrieNodes(pathSet, context.RootHash, 1, default)!;

        Assert.That(result.Count, Is.EqualTo(1));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void TestNoState(bool withLastNStateTracker)
    {
        ILastNStateRootTracker? lastNStateTracker = null;
        if (withLastNStateTracker)
        {
            lastNStateTracker = Substitute.For<ILastNStateRootTracker>();
            lastNStateTracker.HasStateRoot(Arg.Any<Hash256>()).Returns(false);
        }

        using ISnapServerContext context = CreateContext(lastNStateRootTracker: lastNStateTracker);

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 4000, CancellationToken.None);

        Assert.That(accounts.Count, Is.EqualTo(0));

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
            context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);

        Assert.That(storageSlots.Count, Is.EqualTo(0));

        accounts.Dispose();
        accountProofs.Dispose();
        proofs?.Dispose();
        storageSlots.DisposeRecursive();
    }

    [Test]
    public void TestGetAccountRangeMultiple()
    {
        using ISnapServerContext context = CreateContext();
        FillWithTestAccounts(context);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                context.Server.GetAccountRanges(context.RootHash, startRange, Keccak.MaxValue, 100, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, startRange,
                    accounts, proofs);

                Assert.That(result, Is.EqualTo(AddRangeResult.OK));
                startRange = accounts[^1].Path.ToCommitment();
                if (startRange.Bytes.SequenceEqual(TestItem.Tree.AccountsWithPaths[^1].Path.Bytes))
                {
                    break;
                }
            }
            finally
            {
                accounts.Dispose();
                proofs.Dispose();
            }
        }
        Assert.That(context.PersistedNodeCount, Is.EqualTo(10));
    }

    [TestCase(10, 10)]
    [TestCase(10000, 1000)]
    [TestCase(10000, 10000000)]
    [TestCase(10000, 10000)]
    public void TestGetAccountRangeMultipleLarger(int stateSize, int byteLimit)
    {
        using ISnapServerContext context = CreateContext();
        FillMultipleAccounts(context, stateSize);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                context.Server.GetAccountRanges(context.RootHash, startRange, Keccak.MaxValue, byteLimit, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, startRange,
                    accounts, proofs);

                Assert.That(result, Is.EqualTo(AddRangeResult.OK));
                if (startRange == accounts[^1].Path.ToCommitment())
                {
                    break;
                }

                startRange = accounts[^1].Path.ToCommitment();
            }
            finally
            {
                accounts.Dispose();
                proofs.Dispose();
            }
        }
    }

    [TestCase(10, 10)]
    [TestCase(10000, 10)]
    [TestCase(100, 100)]
    [TestCase(10000, 10000000)]
    public void TestGetAccountRangeArtificialLimit(int stateSize, int byteLimit)
    {
        using ISnapServerContext context = CreateContext();
        FillMultipleAccounts(context, stateSize);
        Hash256 startRange = Keccak.Zero;

        ValueHash256 limit = new("0x8000000000000000000000000000000000000000000000000000000000000000");
        while (true)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = context.Server
                .GetAccountRanges(context.RootHash, startRange, limit, byteLimit, CancellationToken.None);

            try
            {
                AddRangeResult result = context.SnapProvider.AddAccountRange(1, context.RootHash, startRange,
                    accounts, proofs);

                Assert.That(result, Is.EqualTo(AddRangeResult.OK));
                if (startRange == accounts[^1].Path.ToCommitment())
                {
                    break;
                }

                startRange = accounts[^1].Path.ToCommitment();
            }
            finally
            {
                accounts.Dispose();
                proofs.Dispose();
            }
        }
    }

    [Test]
    public void TestGetStorageRange()
    {
        using ISnapServerContext context = CreateContext();
        Hash256 storageRoot = FillAccountWithDefaultStorage(context);

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
            context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);

        try
        {
            StorageRange storageRangeRequest = new()
            {
                StartingHash = Keccak.Zero,
                Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.Tree.AccountsWithPaths[0].Path, new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot)) }
            };
            AddRangeResult result = context.SnapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, storageSlots[0], proofs);

            Assert.That(result, Is.EqualTo(AddRangeResult.OK));
        }
        finally
        {
            storageSlots.DisposeRecursive();
            proofs?.Dispose();
        }
    }

    [Test]
    public void TestGetStorageRange_NoSlotsForAccount()
    {
        using ISnapServerContext context = CreateContext();
        FillAccountWithDefaultStorage(context);

        ValueHash256 lastStorageHash = TestItem.Tree.SlotsWithPaths[^1].Path;
        UInt256 asInt = lastStorageHash.ToUInt256();
        ValueHash256 beyondLast = new((++asInt).ToBigEndian());

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
            context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                beyondLast, ValueKeccak.MaxValue, 10, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(storageSlots.Count, Is.EqualTo(0));
            Assert.That(proofs?.Count, Is.GreaterThan(0)); //in worst case should get at least root node
        }

        storageSlots.DisposeRecursive();
        proofs?.Dispose();
    }

    [Test]
    public void TestGetStorageRangeMulti()
    {
        using ISnapServerContext context = CreateContext();
        Hash256 storageRoot = FillAccountWithStorage(context, 10000);

        Hash256 startRange = Keccak.Zero;
        while (true)
        {
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> storageSlots, IByteArrayList? proofs) =
                context.Server.GetStorageRanges(context.RootHash, [TestItem.Tree.AccountsWithPaths[0]],
                    startRange, ValueKeccak.MaxValue, 10000, CancellationToken.None);

            try
            {
                StorageRange storageRangeRequest = new()
                {
                    StartingHash = startRange,
                    Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.Tree.AccountsWithPaths[0].Path, new Account(UInt256.Zero).WithChangedStorageRoot(storageRoot)) }
                };
                AddRangeResult result = context.SnapProvider.AddStorageRangeForAccount(storageRangeRequest, 0, storageSlots[0], proofs);

                Assert.That(result, Is.EqualTo(AddRangeResult.OK));
                if (startRange == storageSlots[0][^1].Path.ToCommitment())
                {
                    break;
                }

                startRange = storageSlots[0][^1].Path.ToCommitment();
            }
            finally
            {
                storageSlots.DisposeRecursive();
                proofs?.Dispose();
            }
        }
    }

    [Test]
    public void TestWithHugeTree()
    {
        using ISnapServerContext context = CreateContext();

        // generate Remote Tree
        using (IWriteBatch batch = context.BeginWriteBatch())
        {
            for (int accountIndex = 0; accountIndex < 10000; accountIndex++)
                batch.SetAccount(TestItem.GetRandomAddress(), TestItem.GenerateRandomAccount());
        }

        List<PathWithAccount> accountWithStorage = [];
        using (IWriteBatch batch = context.BeginWriteBatch())
        {
            for (int i = 1000; i < 10000; i += 1000)
            {
                Address address = TestItem.GetRandomAddress();
                Hash256 storagePath = address.ToAccountPath.ToCommitment();
                for (int j = 0; j < i; j += 1)
                    batch.SetSlot(storagePath, TestItem.GetRandomKeccak(), TestItem.GetRandomKeccak().Bytes.ToArray());
                batch.SetAccount(address, TestItem.GenerateRandomAccount());
                accountWithStorage.Add(new PathWithAccount(address.ToAccountPath, new Account(0)));
            }
        }

        // size of one PathWithAccount ranges from 39 -> 72
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList accountProofs)
            = context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 10, CancellationToken.None);
        Assert.That(accounts.Count, Is.EqualTo(1));
        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 100, CancellationToken.None);
        Assert.That(accounts.Count, Is.GreaterThan(2));
        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 10000, CancellationToken.None);
        Assert.That(accounts.Count, Is.GreaterThan(138));
        accounts.Dispose();
        accountProofs.Dispose();

        // TODO: Double check the threshold
        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 720000, CancellationToken.None);
        Assert.That(accounts.Count, Is.EqualTo(10009));

        accounts.Dispose();
        accountProofs.Dispose();

        (accounts, accountProofs) =
            context.Server.GetAccountRanges(context.RootHash, Keccak.Zero, Keccak.MaxValue, 10000000, CancellationToken.None);
        Assert.That(accounts.Count, Is.EqualTo(10009));
        accounts.Dispose();
        accountProofs.Dispose();

        PathWithAccount[] accountWithStorageArray = accountWithStorage.ToArray();

        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..1], ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);
        Assert.That(slots.Count, Is.EqualTo(1));
        Assert.That(slots[0].Count, Is.EqualTo(1));
        Assert.That(proofs, Is.Not.Null);

        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..1], ValueKeccak.Zero, ValueKeccak.MaxValue, 1000000, CancellationToken.None);
        Assert.That(slots.Count, Is.EqualTo(1));
        Assert.That(slots[0].Count, Is.EqualTo(1000));
        Assert.That(proofs?.Count, Is.EqualTo(0));

        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..2], ValueKeccak.Zero, ValueKeccak.MaxValue, 10, CancellationToken.None);
        Assert.That(slots.Count, Is.EqualTo(1));
        Assert.That(slots[0].Count, Is.EqualTo(1));
        Assert.That(proofs, Is.Not.Null);
        slots.DisposeRecursive();
        proofs?.Dispose();

        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray[..2], ValueKeccak.Zero, ValueKeccak.MaxValue, 100000, CancellationToken.None);
        Assert.That(slots.Count, Is.EqualTo(2));
        Assert.That(slots[0].Count, Is.EqualTo(1000));
        Assert.That(slots[1].Count, Is.EqualTo(539));
        Assert.That(proofs, Is.Not.Null);
        slots.DisposeRecursive();
        proofs?.Dispose();


        // incomplete tree will be returned as the hard limit is 2000000
        (slots, proofs) = context.Server.GetStorageRanges(context.RootHash, accountWithStorageArray, ValueKeccak.Zero, ValueKeccak.MaxValue, 3000000, CancellationToken.None);
        Assert.That(slots.Count, Is.EqualTo(8));
        Assert.That(slots[^1].Count, Is.LessThan(8000));
        Assert.That(proofs?.Count, Is.GreaterThan(0));

        slots.DisposeRecursive();
        proofs?.Dispose();
    }
}
