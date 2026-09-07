// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Proofs;
using Nethermind.State.SnapServer;
using Nethermind.Trie.Pruning;
using Nethermind.Trie;
using AccountRange = Nethermind.State.Snap.AccountRange;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapProviderTests
{

    private ContainerBuilder CreateContainerBuilder(
        TestSyncConfig? testSyncConfig = null,
        Func<INodeStorage, ILogManager, ISnapTrieFactory>? factoryCreator = null) =>
        new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(testSyncConfig ?? new TestSyncConfig(), factoryCreator));

    private IContainer CreateContainer(TestSyncConfig? testSyncConfig = null) =>
        CreateContainerBuilder(testSyncConfig).Build();

    [Test]
    public void AddAccountRange_AccountListIsEmpty_ThrowArgumentException()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        Assert.That(
            () => snapProvider.AddAccountRange(
                0,
                Keccak.Zero,
                Keccak.Zero,
                Array.Empty<PathWithAccount>(),
                EmptyByteArrayList.Instance), Throws.ArgumentException);
    }

    [Test]
    public void AddAccountRange_ResponseHasEmptyListOfAccountsAndOneProof_ReturnsExpiredRootHash()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        using AccountsAndProofs accountsAndProofs = new();
        AccountRange accountRange = new(Keccak.Zero, Keccak.Zero, Keccak.MaxValue);
        accountsAndProofs.PathAndAccounts = new List<PathWithAccount>().ToPooledList();
        accountsAndProofs.Proofs = new ByteArrayListAdapter(new List<byte[]> { new byte[] { 0x0 } }.ToPooledList());

        Assert.That(snapProvider.AddAccountRange(accountRange, accountsAndProofs), Is.EqualTo(AddRangeResult.ExpiredRootHash));
    }

    [Test]
    public void AddStorageRange_ResponseReversedOrderedListOfAccounts_ReturnsInvalidOrder()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        StorageRange storage = new()
        {
            Accounts = new PathWithAccount[] { new(TestItem.KeccakA, Account.TotallyEmpty) }.ToPooledList(),
        };
        List<PathWithStorageSlot> slots =
        [
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000004"), []),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000003"), []),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000002"), []),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000001"), []),
        ];

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storage,
            0,
            slots,
            null), Is.EqualTo(AddRangeResult.InvalidOrder));

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
    }

    [Test]
    public void AddStorageRange_EmptySlotsList_ReturnsEmptySlots()
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        StorageRange storage = new()
        {
            Accounts = new PathWithAccount[] { new(TestItem.KeccakA, Account.TotallyEmpty) }.ToPooledList(),
        };

        // Test with empty slots list
        List<PathWithStorageSlot> emptySlots = [];

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storage,
            0,
            emptySlots,
            null), Is.EqualTo(AddRangeResult.EmptyRange));

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
    }

    [TestCase(1, 2, AddRangeResult.OutOfBounds)]
    [TestCase(2, 3, AddRangeResult.OutOfBounds)]
    [TestCase(4, 64, AddRangeResult.OutOfBounds)]
    [TestCase(1, 1, AddRangeResult.EmptyRange)]
    [TestCase(4, 4, AddRangeResult.EmptyRange)]
    [TestCase(4, 2, AddRangeResult.EmptyRange)]
    public void AddStorageRange_RejectsResponseOnlyWhenSlotListsExceedRequestedAccounts(int accountCount, int slotListCount, AddRangeResult expected)
    {
        using IContainer container = CreateContainer();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        using StorageRange request = CreateStorageRange(accountCount);
        using SlotsAndProofs response = CreateEmptySlotsResponse(slotListCount);

        Assert.That(snapProvider.AddStorageRange(request, response), Is.EqualTo(expected));
    }

    [Test]
    public void AddStorageRange_ResponseHasMoreSlotListsThanRequestedAccounts_KeepsRangePhaseCompletable()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Account.TotallyEmpty);
        progressTracker.EnqueueAccountStorage(account);

        DrainAccountRangePartition(progressTracker);

        progressTracker.IsFinished(out SnapSyncBatch? storageBatch);
        storageBatch!.StorageRangeResponse = CreateEmptySlotsResponse(storageBatch.StorageRangeRequest!.Accounts.Count + 1);

        Assert.That(snapProvider.AddStorageRange(storageBatch.StorageRangeRequest, storageBatch.StorageRangeResponse), Is.EqualTo(AddRangeResult.OutOfBounds));
        snapProvider.ReleaseRequest(storageBatch, responseHandled: true);
        storageBatch.Dispose();

        progressTracker.IsFinished(out SnapSyncBatch? retryBatch);
        Assert.That(retryBatch!.StorageRangeRequest!.Accounts.AsSpan()[0].Path, Is.EqualTo(account.Path));

        progressTracker.ReportStorageRequestFinished(retryBatch.StorageRangeRequest.Accounts.Count);
        retryBatch.Dispose();

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True);
    }

    [Test]
    public void AddStorageRange_ResponseCoversFewerAccountsThanRequested_QueuesTheRestAgain()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount covered = new(TestItem.ValueKeccaks[0], Account.TotallyEmpty);
        PathWithAccount uncovered = new(TestItem.ValueKeccaks[1], Account.TotallyEmpty);
        progressTracker.EnqueueAccountStorage(covered);
        progressTracker.EnqueueAccountStorage(uncovered);
        DrainAccountRangePartition(progressTracker);

        progressTracker.IsFinished(out SnapSyncBatch? batch);
        Assert.That(batch!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
        batch.StorageRangeResponse = CreateEmptySlotsResponse(1);

        snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse);
        snapProvider.ReleaseRequest(batch, responseHandled: true);
        batch.Dispose();

        // The covered account failed verification and goes for a refresh; the uncovered one comes back.
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
        progressTracker.ReportAccountRefreshFinished();
        refresh.Dispose();

        progressTracker.IsFinished(out SnapSyncBatch? retried);
        Assert.That(retried!.StorageRangeRequest!.Accounts.AsSpan()[0].Path, Is.EqualTo(uncovered.Path));
        progressTracker.ReportStorageRequestFinished(retried.StorageRangeRequest.Accounts.Count);
        retried.Dispose();

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True);
    }

    [TestCase(nameof(SnapSyncBatch.AccountRangeRequest))]
    [TestCase(nameof(SnapSyncBatch.StorageRangeRequest))]
    [TestCase(nameof(SnapSyncBatch.CodesRequest))]
    public void HandleResponse_ProcessingThrows_OffersTheRequestAgain(string requestKind)
    {
        using IContainer container = CreateContainerBuilder(
                new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 },
                (_, _) => new TestSnapTrieFactory(
                    static () => throw new IOException("state backend unavailable"),
                    static () => throw new IOException("state backend unavailable")))
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        ISimpleSyncFeed<SnapSyncBatch> feed = container.Resolve<ISimpleSyncFeed<SnapSyncBatch>>();

        ValueHash256 work = TestItem.ValueKeccaks[0];
        switch (requestKind)
        {
            case nameof(SnapSyncBatch.StorageRangeRequest):
                progressTracker.EnqueueAccountStorage(new(work, Account.TotallyEmpty));
                DrainAccountRangePartition(progressTracker);
                break;
            case nameof(SnapSyncBatch.CodesRequest):
                progressTracker.EnqueueCodeHash(work);
                DrainAccountRangePartition(progressTracker);
                break;
        }

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.False);
        ValueHash256? issuedLimit = batch!.AccountRangeRequest?.LimitHash;
        AttachThrowingResponse(batch);

        // The feed disposes the batch itself.
        Assert.That(() => feed.HandleResponse(batch, null), Throws.InstanceOf<IOException>());

        // Assert the same work came back, then consume it so only the active count can keep the phase open.
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? retried), Is.False);
        switch (requestKind)
        {
            case nameof(SnapSyncBatch.AccountRangeRequest):
                Assert.That(retried!.AccountRangeRequest?.LimitHash, Is.EqualTo(issuedLimit));
                progressTracker.UpdateAccountRangePartitionProgress(issuedLimit!.Value, Keccak.MaxValue, false);
                progressTracker.ReportAccountRangePartitionFinished(issuedLimit.Value);
                break;
            case nameof(SnapSyncBatch.StorageRangeRequest):
                Assert.That(retried!.StorageRangeRequest?.Accounts.AsSpan()[0].Path, Is.EqualTo(work));
                progressTracker.ReportStorageRequestFinished(retried.StorageRangeRequest!.Accounts.Count);
                break;
            case nameof(SnapSyncBatch.CodesRequest):
                Assert.That(retried!.CodesRequest?.AsSpan()[0], Is.EqualTo(work));
                progressTracker.ReportCodeRequestFinished([]);
                break;
        }

        retried!.Dispose();

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True,
            "the work was queued again but its active request count was never released");
    }

    private static void AttachThrowingResponse(SnapSyncBatch batch)
    {
        if (batch.AccountRangeRequest is not null)
        {
            batch.AccountRangeResponse = new AccountsAndProofs
            {
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.ValueKeccaks[0], Account.TotallyEmpty) },
                Proofs = EmptyByteArrayList.Instance
            };
        }
        else if (batch.StorageRangeRequest is not null)
        {
            batch.StorageRangeResponse = CreateEmptySlotsResponse(batch.StorageRangeRequest.Accounts.Count);
        }
        else if (batch.CodesRequest is not null)
        {
            batch.CodesResponse = new ThrowingByteArrayList();
        }
    }

    private sealed class ThrowingByteArrayList : IByteArrayList
    {
        public int Count => 1;
        public ReadOnlySpan<byte> this[int index] => throw new IOException("code stream unavailable");
        public void Dispose() { }
    }

    [Test]
    public void AddStorageRange_ShouldPersistEntries()
    {
        const int slotCount = 6;
        TestMemDb stateDb = new();
        TestRawTrieStore store = new(stateDb);

        // Build storage tree with RLP-encoded 32-byte values
        Hash256 accountHash = TestItem.Tree.AccountAddress0;
        StorageTree storageTree = new(store.GetTrieStore(accountHash), LimboLogs.Instance);
        PathWithStorageSlot[] slots = new PathWithStorageSlot[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            ValueHash256 slotKey = Keccak.Compute(i.ToBigEndianByteArray());
            byte[] value = (i + 1).ToBigEndianByteArray();
            byte[] rlpValue = Rlp.Encode(value).Bytes;
            storageTree.Set(slotKey, rlpValue, false);
            slots[i] = new PathWithStorageSlot(slotKey, rlpValue);
        }
        storageTree.Commit();
        Array.Sort(slots, (a, b) => a.Path.CompareTo(b.Path));

        StateTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        stateTree.Set(accountHash, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
        stateTree.Commit();

        // Collect proofs
        AccountProofCollector proofCollector = new(accountHash.Bytes,
            new ValueHash256[] { Keccak.Zero, slots[^1].Path });
        stateTree.Accept(proofCollector, stateTree.RootHash);
        AccountProof proof = proofCollector.BuildResult();

        using IContainer container = CreateContainer();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        StorageRange storageRange = new()
        {
            StartingHash = Keccak.Zero,
            Accounts = new ArrayPoolList<PathWithAccount>(1)
            {
                new(accountHash, new Account(0, 1).WithChangedStorageRoot(storageTree.RootHash))
            },
        };

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storageRange, 0, slots,
            new ByteArrayListAdapter(proof.StorageProofs[0].Proof.Concat(proof.StorageProofs[1].Proof).ToArray().ToPooledList())), Is.EqualTo(AddRangeResult.OK));
    }

    [Test]
    public void AddAccountRange_SetStartRange_ToAfterLastPath()
    {
        (Hash256, Account)[] entries =
        [
            (TestItem.KeccakA, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakB, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakC, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakD, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakE, TestItem.GenerateRandomAccount()),
            (TestItem.KeccakF, TestItem.GenerateRandomAccount()),
        ];
        Array.Sort(entries, static (e1, e2) => e1.Item1.CompareTo(e2.Item1));

        (ISnapStateServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = ss.GetAccountRanges(
            root, Keccak.Zero, entries[3].Item1, 1.MB, default);

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.EqualTo(false));

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        Assert.That(snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));
        snapProvider.ReleaseRequest(batch!, responseHandled: true);
        Assert.That(progressTracker.IsFinished(out batch), Is.EqualTo(false));
        ValueHash256 startingHash = batch!.AccountRangeRequest!.StartingHash;
        Assert.That(startingHash.CompareTo(entries[3].Item1), Is.GreaterThan(0));
        Assert.That(startingHash.CompareTo(entries[4].Item1), Is.LessThan(0));
    }

    [Test]
    public void AddAccountRange_ShouldNotStoreStorageAfterLimit()
    {
        (Hash256, Account)[] entries =
        [
            (new Hash256("0fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("2fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("7fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            // Should split it right here

            (new Hash256("9fffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("afffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
            (new Hash256("ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"), TestItem.GenerateRandomAccount().WithChangedStorageRoot(TestItem.GetRandomKeccak())),
        ];
        Array.Sort(entries, static (e1, e2) => e1.Item1.CompareTo(e2.Item1));

        (ISnapStateServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 2
        })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = ss.GetAccountRanges(
            root, Keccak.Zero, Keccak.MaxValue, 1.MB, default);

        // The range given out here should be half.
        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.EqualTo(false));

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        Assert.That(snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));

        Assert.That(container.ResolveNamed<IDb>(DbNames.State).GetAllKeys().Count(), Is.EqualTo(3)); // 3 child. Root branch node not saved due to state sync compatibility
    }

    [TestCase("badreq-roothash.zip")]
    [TestCase("badreq-roothash-2.zip")]
    [TestCase("badreq-roothash-3.zip")]
    [TestCase("badreq-trieexception.zip")]
    public void Test_EdgeCases(string testFileName)
    {
        using DeflateStream decompressor =
            new(
                GetType().Assembly
                    .GetManifestResourceStream($"Nethermind.Synchronization.Test.SnapSync.TestFixtures.{testFileName}")!,
                CompressionMode.Decompress);
        BadReq asReq = JsonSerializer.Deserialize<BadReq>(decompressor)!;
        AccountDecoder acd = new();
        Account[] accounts = new Account[asReq.Accounts.Count];
        for (int i = 0; i < accounts.Length; i++)
        {
            RlpReader context = new(Bytes.FromHexString(asReq.Accounts[i]));
            accounts[i] = acd.Decode(ref context)!;
        }

        ValueHash256[] paths = asReq.Paths.Select((bt) => new ValueHash256(Bytes.FromHexString(bt))).ToArray();
        List<PathWithAccount> pathWithAccounts = accounts.Select((acc, idx) => new PathWithAccount(paths[idx], acc)).ToList();
        List<byte[]> proofs = asReq.Proofs.Select((str) => Bytes.FromHexString(str)).ToList();

        TestMemDb db = new();
        NodeStorage nodeStorage = new(db);
        SnapUpperBoundAdapter adapter = new(new RawScopedTrieStore(nodeStorage));
        StateTree stree = new(adapter, LimboLogs.Instance);
        TestSnapTrieFactory factory = new(() => new PatriciaSnapStateTree(stree, adapter, nodeStorage));
        Assert.That(SnapProviderHelper.AddAccountRange(
                factory,
                0,
                new ValueHash256(asReq.Root),
                new ValueHash256(asReq.StartingHash),
                new ValueHash256(asReq.LimitHash),
                pathWithAccounts,
                new ByteArrayListAdapter(proofs.ToPooledList())).result, Is.EqualTo(AddRangeResult.OK));
    }

    private record BadReq(
        string Root,
        string StartingHash,
        string LimitHash,
        List<string> Proofs,
        List<string> Paths,
        List<string> Accounts
    );

    private static void DrainAccountRangePartition(ProgressTracker progressTracker)
    {
        progressTracker.IsFinished(out SnapSyncBatch? batch);
        ValueHash256 partitionLimit = batch!.AccountRangeRequest!.LimitHash!.Value;
        progressTracker.UpdateAccountRangePartitionProgress(partitionLimit, Keccak.MaxValue, false);
        progressTracker.ReportAccountRangePartitionFinished(partitionLimit);
        batch.Dispose();
    }

    private static StorageRange CreateStorageRange(int accountCount)
    {
        ArrayPoolList<PathWithAccount> accounts = new(accountCount);
        for (int i = 0; i < accountCount; i++)
        {
            accounts.Add(new PathWithAccount(TestItem.ValueKeccaks[i], Account.TotallyEmpty));
        }

        return new StorageRange { Accounts = accounts, StartingHash = Keccak.Zero };
    }

    private static SlotsAndProofs CreateEmptySlotsResponse(int slotListCount)
    {
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> pathsAndSlots = new(slotListCount);
        for (int i = 0; i < slotListCount; i++)
        {
            pathsAndSlots.Add(new ArrayPoolList<PathWithStorageSlot>(0));
        }

        return new SlotsAndProofs { PathsAndSlots = pathsAndSlots, Proofs = EmptyByteArrayList.Instance };
    }

    private static (ISnapStateServer, Hash256) BuildSnapServerFromEntries((Hash256, Account)[] entries)
    {
        TestMemDb stateDb = new();
        TestRawTrieStore trieStore = new(stateDb);
        StateTree st = new(trieStore, LimboLogs.Instance);
        {
            using IBlockCommitter _ = trieStore.BeginBlockCommit(0);
            foreach ((Hash256, Account) entry in entries)
            {
                st.Set(entry.Item1, entry.Item2);
            }
            st.Commit();
        }

        SnapStateServer ss = new(trieStore.AsReadOnly(), LimboLogs.Instance);
        return (ss, st.RootHash);
    }
}
