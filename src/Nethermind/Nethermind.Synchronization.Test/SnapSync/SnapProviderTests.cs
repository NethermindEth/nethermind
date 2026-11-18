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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using ConcurrentCollections;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Nethermind.State.Flat.Sync;
using Nethermind.State.Flat.Sync.Snap;
using Nethermind.State.Healing;
using Nethermind.State.Proofs;
using Nethermind.State.SnapServer;
using Nethermind.Synchronization.FastSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Test.FastSync;
using NSubstitute;
using Nethermind.Trie.Pruning;
using Nethermind.Trie;
using AccountRange = Nethermind.State.Snap.AccountRange;

using Nethermind.Stats.Model;

using Nethermind.Stats;

namespace Nethermind.Synchronization.Test.SnapSync;

[TestFixture]
public class SnapProviderTests
{
    [Test]
    public void Alternate_nonempty_proof_reconstructs_omitted_interiors_and_rejects_missing_boundaries(
        [Values(0, 1, 2, 3)] int shape)
    {
        using MemDb serverDb = new();
        TestRawTrieStore store = new(serverDb);
        StateTree state = new(store, LimboLogs.Instance);
        PathWithStorageSlot[] slots = new PathWithStorageSlot[512];
        StorageTree storage = new(store.GetTrieStore(TestItem.KeccakA), LimboLogs.Instance);
        using (store.BeginBlockCommit(1))
        {
            for (int i = 0; i < slots.Length; i++)
            {
                ValueHash256 path = Keccak.Compute(BitConverter.GetBytes(i));
                storage.Set(path, [1], false);
                slots[i] = new(path, [1]);
            }
            storage.Commit();
            state.Set(TestItem.KeccakA, new Account(0, 1, storage.RootHash, Keccak.OfAnEmptyString));
            state.Commit();
        }
        Array.Sort(slots, static (left, right) => left.Path.CompareTo(right.Path));
        SnapStateServer server = new(store.AsReadOnly(), LimboLogs.Instance);
        using StorageRange request = new()
        {
            RootHash = state.RootHash,
            Accounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.KeccakA, new Account(0, 1, TestItem.KeccakB, Keccak.OfAnEmptyString).ToStruct()) },
            StartingHash = slots[128].Path,
            LimitHash = slots[383].Path
        };
        (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> values, IByteArrayList? supplied) = server.GetStorageRanges(
            request.RootHash, request.Accounts, request.StartingHash, request.LimitHash, 2_000_000, CancellationToken.None);
        using SlotsAndProofs response = new() { PathsAndSlots = values, Proofs = supplied };
        Assert.That(response.PathsAndSlots, Has.Count.EqualTo(1));
        IByteArrayList originalProofs = response.Proofs ?? EmptyByteArrayList.Instance;
        Assert.That(originalProofs.Count, Is.GreaterThan(1).And.LessThan(response.PathsAndSlots[0].Count),
            "the honest server supplies boundaries while the returned slots reconstruct omitted interior nodes");
        List<byte[]> proofs = [with(originalProofs.Count)];
        for (int i = 0; i < originalProofs.Count; i++) proofs.Add(originalProofs[i].ToArray());
        if (shape == 1)
        {
            byte[] root = proofs.Single(proof => ValueKeccak.Compute(proof) == storage.RootHash);
            proofs = [root];
        }
        else if (shape == 2)
        {
            using MemDb detachedDb = new();
            RawScopedTrieStore detachedStore = new(detachedDb);
            StorageTree detached = new(detachedStore, LimboLogs.Instance);
            detached.Set(TestItem.KeccakD, [7], false);
            detached.Commit();
            byte[] disconnected = detachedStore.LoadRlp(TreePath.Empty, detached.RootHash)!;
            Assert.That(ValueKeccak.Compute(disconnected), Is.EqualTo(detached.RootHash.ValueHash256));
            proofs.Add(disconnected);
        }
        using IByteArrayList checkedProofs = new ByteArrayListAdapter(proofs.ToPooledList());
        IReadOnlyList<PathWithStorageSlot> checkedSlots = response.PathsAndSlots[0];
        if (shape == 3)
        {
            PathWithStorageSlot[] changed = checkedSlots.ToArray();
            changed[changed.Length / 2] = new(changed[changed.Length / 2].Path, [2]);
            checkedSlots = changed;
        }
        using IContainer isolated = ProgressTrackerTests.CreateContainer();
        int persistedBefore = isolated.Resolve<IDbProvider>().StateDb.GetAll().Count();
        AddRangeResult result = SnapProviderHelper.VerifyAlternateStorageRange(isolated.Resolve<ISnapTrieFactory>(),
            request.Accounts[0].Path, checkedSlots, request.StartingHash, request.LimitHash, checkedProofs);
        Assert.That(result, Is.EqualTo(shape == 0 ? AddRangeResult.OK : shape is 1 or 3 ? AddRangeResult.DifferentRootHash : AddRangeResult.InvalidProof));
        if (shape != 0) Assert.That(isolated.Resolve<IDbProvider>().StateDb.GetAll().Count(), Is.EqualTo(persistedBefore));
    }

    [Test]
    [NonParallelizable]
    public void Account_evidence_allocation_tracks_only_accounts_with_storage()
    {
        using StorageRange storage = ProgressTrackerTests.CreateRequest(1);
        PathWithAccount[] accounts = new PathWithAccount[4096];
        accounts[0] = storage.Accounts[0];
        ValueHash256 path = ValueKeccak.Zero;
        for (int i = 1; i < accounts.Length; i++)
        {
            path = path.IncrementPath();
            accounts[i] = new(path, new Account(0, 1).ToStruct());
        }
        Array.Sort(accounts, static (first, second) => first.Path.CompareTo(second.Path));
        using MemDb db = new();
        StateTree state = new(new RawScopedTrieStore(db), LimboLogs.Instance);
        foreach (PathWithAccount account in accounts) state.Set(account.Path, account.Account.ToAccount());
        state.UpdateRootHash();
        using IContainer measured = ProgressTrackerTests.CreateContainer(root: state.RootHash);
        SnapProvider provider = measured.Resolve<SnapProvider>();
        ISnapTrieFactory factory = measured.Resolve<ISnapTrieFactory>();
        for (int i = 0; i < 2; i++)
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(SnapProviderHelper.AddAccountRange(factory, 1, state.RootHash, ValueKeccak.Zero,
                    ValueKeccak.MaxValue, accounts).result, Is.EqualTo(AddRangeResult.OK));
                Assert.That(provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, accounts), Is.EqualTo(AddRangeResult.OK));
            }
        }
        long verificationBytes = long.MaxValue;
        long providerBytes = long.MaxValue;
        for (int i = 0; i < 5; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            AddRangeResult direct = SnapProviderHelper.AddAccountRange(factory, 1, state.RootHash, ValueKeccak.Zero,
                ValueKeccak.MaxValue, accounts).result;
            long verificationSample = GC.GetAllocatedBytesForCurrentThread() - before;
            verificationBytes = Math.Min(verificationBytes, verificationSample);
            before = GC.GetAllocatedBytesForCurrentThread();
            AddRangeResult provided = provider.AddAccountRange(1, state.RootHash, ValueKeccak.Zero, accounts);
            long providerSample = GC.GetAllocatedBytesForCurrentThread() - before;
            providerBytes = Math.Min(providerBytes, providerSample);
            TestContext.Out.WriteLine($"Allocation sample {i}: verifier={verificationSample}, provider={providerSample}");
            using (Assert.EnterMultipleScope())
            {
                Assert.That(direct, Is.EqualTo(AddRangeResult.OK));
                Assert.That(provided, Is.EqualTo(AddRangeResult.OK));
            }
        }
        Assert.That(providerBytes - verificationBytes, Is.LessThan(32_768),
            "provider metadata must not allocate an evidence object for each storage-free account");
        using IContainer behavioral = ProgressTrackerTests.CreateContainer(root: state.RootHash);
        ProgressTracker tracker = behavioral.Resolve<ProgressTracker>();
        SnapProvider verifiedProvider = behavioral.Resolve<SnapProvider>();
        tracker.IsFinished(out SnapSyncBatch? discovery);
        using (discovery)
        {
            using AccountsAndProofs response = new() { PathAndAccounts = new ArrayPoolList<PathWithAccount>(accounts) };
            Assert.That(verifiedProvider.AddAccountRange(discovery!.AccountRangeRequest!, response), Is.EqualTo(AddRangeResult.OK));
            verifiedProvider.ReleaseRequest(discovery, responseHandled: true);
        }
        tracker.IsFinished(out SnapSyncBatch? requestedStorage);
        using (requestedStorage)
        {
            Assert.That(requestedStorage!.StorageRangeRequest!.Accounts, Is.EqualTo(new[] { storage.Accounts[0] }));
            using SlotsAndProofs response = new()
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { ProgressTrackerTests.ValidSlots() }
            };
            Assert.That(verifiedProvider.AddStorageRange(requestedStorage.StorageRangeRequest, response), Is.EqualTo(AddRangeResult.OK));
            verifiedProvider.ReleaseRequest(requestedStorage, responseHandled: true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
    }

    public enum EmptyStorageProof
    {
        Valid,
        Junk,
        Missing,
        CoversExistingSlot
    }

    [Test]
    public void Empty_storage_proof_preserves_continuation_and_only_completes_proven_work(
        [Values] EmptyStorageProof proofKind, [Values] bool anotherAccount, [Values] bool branched, [Values] bool bounded)
    {
        ValueHash256 start = TestItem.KeccakB;
        TestMemDb db = new();
        TestRawTrieStore store = new(db);
        StorageTree storage = new(store.GetTrieStore(TestItem.KeccakA), LimboLogs.Instance);
        storage.Set(proofKind == EmptyStorageProof.CoversExistingSlot ? start : ValueKeccak.Zero, [1], false);
        if (branched) storage.Set(ValueKeccak.Zero.IncrementPath(), [2], false);
        if (bounded) storage.Set(ValueKeccak.MaxValue, [3], false);
        storage.Commit();
        PathWithAccount account = new(TestItem.KeccakA, Build.An.Account.WithStorageRoot(storage.RootHash).TestObject.ToStruct());
        StateTree state = new(store.GetTrieStore(null), LimboLogs.Instance);
        state.Set(account.Path, account.Account.ToAccount());
        state.Commit();
        AccountProofCollector collector = new(account.Path.Bytes, [start]);
        state.Accept(collector, state.RootHash);
        byte[][] proof = collector.BuildResult().StorageProofs[0].Proof;
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(state.RootHash).Build();
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(tracker);
        RegisterLargeStorage(provider, tracker, account, bounded ? 2 : 1);
        ArrayPoolList<PathWithAccount> accounts = [with(2), account];
        if (anotherAccount) accounts.Add(new(TestItem.KeccakC, account.Account));
        ValueHash256 limit = bounded ? start.IncrementPath() : ValueKeccak.MaxValue;
        using StorageRange request = new() { Accounts = accounts, StartingHash = start, LimitHash = limit };
        using SlotsAndProofs response = new()
        {
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(proofKind switch
            {
                EmptyStorageProof.Junk => [[0xff]],
                EmptyStorageProof.Missing => [],
                _ => proof
            }))
        };
        AddRangeResult expected = proofKind switch
        {
            EmptyStorageProof.Valid => AddRangeResult.OK,
            EmptyStorageProof.Missing => AddRangeResult.ExpiredRootHash,
            _ => AddRangeResult.InvalidProof
        };

        Assert.That(provider.AddStorageRange(request, response), Is.EqualTo(expected));

        bool completed = proofKind == EmptyStorageProof.Valid;
        Assert.That(tracker.LargeStorageProgressCount, Is.EqualTo(completed && !bounded ? 0 : 1));
        tracker.IsFinished(out SnapSyncBatch? next);
        using (next)
        {
            if (completed && !anotherAccount) Assert.That(next, Is.Null);
            else
            {
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(next!.StorageRangeRequest, Is.Not.Null);
                    Assert.That(next.StorageRangeRequest!.Accounts[0].Path, Is.EqualTo(completed ? TestItem.KeccakC.ValueHash256 : account.Path));
                    Assert.That(next.StorageRangeRequest.StartingHash ?? ValueKeccak.Zero, Is.EqualTo(completed ? ValueKeccak.Zero : start));
                    Assert.That(next.StorageRangeRequest.LimitHash ?? ValueKeccak.MaxValue, Is.EqualTo(completed ? ValueKeccak.MaxValue : limit));
                }
                provider.ReleaseRequest(next, responseHandled: true);
            }
        }
        if (completed && bounded)
        {
            tracker.OnCompletedLargeStorage(account);
            Assert.That(tracker.LargeStorageProgressCount, Is.Zero);
        }
    }

    private ContainerBuilder CreateContainerBuilder(
        TestSyncConfig? testSyncConfig = null,
        Func<INodeStorage, ILogManager, ISnapTrieFactory>? factoryCreator = null) =>
        new ContainerBuilder()
            .AddModule(new TestSynchronizerModule(testSyncConfig ?? new TestSyncConfig(), factoryCreator));

    [Test]
    public void Restarted_account_range_cannot_fill_omitted_proof_from_retained_state([Values] bool retainedState)
    {
        using TestMemDb db = new();
        NodeStorage nodes = new(db);
        StateTree state = new(new RawScopedTrieStore(nodes, null), LimboLogs.Instance);
        PathWithAccount first = new(ValueKeccak.Zero, Build.An.Account.WithStorageRoot(TestItem.KeccakA).WithCode([0x60, 0x01]).TestObject.ToStruct());
        PathWithAccount omitted = new(ValueKeccak.Zero.IncrementPath(), first.Account);
        state.Set(first.Path, first.Account.ToAccount());
        state.Set(omitted.Path, omitted.Account.ToAccount());
        state.Commit();
        AccountProofCollector collector = new(first.Path.Bytes);
        state.Accept(collector, state.RootHash);
        byte[][] proof = collector.BuildResult().Proof;
        Assert.That(proof.Length, Is.GreaterThan(1));
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 },
                (store, logs) => new PatriciaSnapTrieFactory(retainedState ? nodes : store, NullDb.Instance, logs))
            .WithSuggestedHeaderOfStateRoot(state.RootHash).Build();
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapSyncBatch? batch;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes.KeyExists(omitted.Path.ToCommitment(), TreePath.Empty, omitted.Account!.StorageRoot), Is.False);
            Assert.That(container.Resolve<IDbProvider>().CodeDb.KeyExists(omitted.Account.CodeHash!.Bytes), Is.False);
            Assert.That(tracker.IsFinished(out batch), Is.False);
        }
        using (batch)
        using (AccountsAndProofs response = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { first },
            Proofs = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(1) { proof[0] })
        })
        {
            Assert.That(provider.AddAccountRange(batch!.AccountRangeRequest!, response), Is.EqualTo(AddRangeResult.InvalidProof));
            object? stack = typeof(PatriciaTree).GetField("_threadStaticTraverseStack", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null);
            Assert.That(stack, Is.Not.Null, "failed proof reconstruction must return its traversal stack");
            Assert.That(stack!.GetType().GetProperty("Count")!.GetValue(stack), Is.Zero);
            provider.ReleaseRequest(batch, responseHandled: true);
        }
        SnapSyncBatch? retry;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.False);
            Assert.That(tracker.IsFinished(out retry), Is.False);
        }
        using (retry)
        using (AccountsAndProofs complete = new()
        {
            PathAndAccounts = new ArrayPoolList<PathWithAccount>(2) { first, omitted },
            Proofs = EmptyByteArrayList.Instance
        })
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(retry!.AccountRangeRequest!.StartingHash, Is.EqualTo(ValueKeccak.Zero));
                Assert.That(provider.AddAccountRange(retry.AccountRangeRequest, complete), Is.EqualTo(AddRangeResult.OK));
            }
            provider.ReleaseRequest(retry, responseHandled: true);
        }
        Assert.That(tracker.IsSnapGetRangesFinished(), Is.False, "storage and code still need to be retrieved after honest recovery");
    }

    private IContainer CreateContainer(TestSyncConfig? testSyncConfig = null) =>
        CreateContainerBuilder(testSyncConfig).Build();

    [Test]
    public void AddCodes_counts_only_matching_codes_and_requeues_every_missing_hash([Values] bool includeMatchingCode)
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash).Build();
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        byte[] code = [0x60, 0x01];
        byte[] missingCode = [0x60, 0x02];
        ValueHash256 hash = ValueKeccak.Compute(code);
        ValueHash256 missingHash = ValueKeccak.Compute(missingCode);
        tracker.EnqueueCodeHashes([hash, missingHash]);
        DrainAccountRangePartition(tracker);
        tracker.IsFinished(out SnapSyncBatch? batch);
        using IByteArrayList response = new ByteArrayListAdapter(new ArrayPoolList<byte[]>(
            includeMatchingCode ? [[0xff], code, code] : [[0xff]]));

        (int matched, long bytes) = provider.AddCodesWithResult(batch!.CodesRequest!, response);
        provider.ReleaseRequest(batch, responseHandled: true);
        batch.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(matched, Is.EqualTo(includeMatchingCode ? 1 : 0));
            Assert.That(bytes, Is.EqualTo(includeMatchingCode ? code.Length : 0));
            Assert.That(container.Resolve<IDbProvider>().CodeDb[hash.Bytes], Is.EqualTo(includeMatchingCode ? code : null));
        }
        tracker.IsFinished(out SnapSyncBatch? retry);
        Assert.That(retry!.CodesRequest, Is.EquivalentTo(includeMatchingCode ? new[] { missingHash } : new[] { hash, missingHash }));
        provider.ReleaseRequest(retry, responseHandled: true);
        retry.Dispose();
    }

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
            Accounts = new PathWithAccount[] { new(TestItem.KeccakA, AccountStruct.TotallyEmpty) }.ToPooledList(),
        };
        List<PathWithStorageSlot> slots =
        [
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000004"), [0x01]),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000003"), [0x01]),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000002"), [0x01]),
            new(new ValueHash256("0000000000000000000000000000000000000000000000000000000000000001"), [0x01]),
        ];

#pragma warning disable NUnit2045 // Keep the state-changing response or range admission separate from dependent state checks.
        Assert.That(snapProvider.AddStorageRangeForAccount(
            storage,
            0,
            slots,
            null), Is.EqualTo(AddRangeResult.InvalidOrder));
#pragma warning restore NUnit2045

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
            Accounts = new PathWithAccount[] { new(TestItem.KeccakA, AccountStruct.TotallyEmpty) }.ToPooledList(),
        };

        // Test with empty slots list
        List<PathWithStorageSlot> emptySlots = [];

#pragma warning disable NUnit2045 // Keep the state-changing response or range admission separate from dependent state checks.
        Assert.That(snapProvider.AddStorageRangeForAccount(
            storage,
            0,
            emptySlots,
            null), Is.EqualTo(AddRangeResult.EmptyRange));
#pragma warning restore NUnit2045

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.False);
    }

    [Test]
    public void AddStorageRange_KeepsTheFirstFailureAndCountsAppliedSlots()
    {
        using IContainer container = CreateContainer();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        (PathWithAccount account, PathWithStorageSlot[] slots, _) = BuildAccountWithStorage(2);
        using StorageRange request = new()
        {
            Accounts = new ArrayPoolList<PathWithAccount>(2) { new(TestItem.KeccakA, Account.TotallyEmpty.ToStruct()), account }
        };
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> pathsAndSlots = new(2)
        {
            new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.MaxValue, []) },
            slots.ToPooledList(),
        };
        using SlotsAndProofs response = new() { PathsAndSlots = pathsAndSlots, Proofs = EmptyByteArrayList.Instance };
        long syncedBefore = Nethermind.Synchronization.Metrics.SnapSyncedStorageSlots;

        AddRangeResult result = snapProvider.AddStorageRange(request, response, out bool madeProgress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(AddRangeResult.InvalidProof));
            Assert.That(madeProgress, Is.True);
            Assert.That(Nethermind.Synchronization.Metrics.SnapSyncedStorageSlots - syncedBefore, Is.EqualTo(slots.Length));
        }
    }

    [TestCase(AddRangeResult.OK, true)]
    [TestCase(AddRangeResult.InvalidProof, false)]
    public void AddStorageRange_WorksWithAProviderThatImplementsOnlyTheOriginalOverload(AddRangeResult expected, bool expectedProgress)
    {
        ISnapProvider provider = new LegacySnapProvider(expected);
        using StorageRange request = new();
        using SlotsAndProofs response = new();

        AddRangeResult result = provider.AddStorageRange(request, response, out bool madeProgress);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(expected));
            Assert.That(madeProgress, Is.EqualTo(expectedProgress));
        }
    }

    private sealed class LegacySnapProvider(AddRangeResult result) : ISnapProvider
    {
        public AddRangeResult AddStorageRange(StorageRange request, SlotsAndProofs response) => result;
        public bool IsFinished(out SnapSyncBatch? nextBatch) => throw new NotSupportedException();
        public bool CanSync() => throw new NotSupportedException();
        public AddRangeResult AddAccountRange(AccountRange request, AccountsAndProofs response) => throw new NotSupportedException();
        public void AddCodes(IReadOnlyList<ValueHash256> requestedHashes, IByteArrayList codes) => throw new NotSupportedException();
        public AddRangeResult RefreshAccounts(AccountsToRefreshRequest request, AccountsAndProofs response) => throw new NotSupportedException();
        public IDisposable? BeginResponse() => throw new NotSupportedException();
        public void ReleaseRequest(SnapSyncBatch batch, bool responseHandled, bool madeRangeProgress = false) => throw new NotSupportedException();
        public bool IsSnapGetRangesFinished() => throw new NotSupportedException();
        public void UpdatePivot() => throw new NotSupportedException();
        public void Dispose() { }
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

    // A peer with no slots past a continuation's origin replies with no slot list and only the proof of the origin -
    // geth's server and ours both do. That proves the rest of the range empty, so the account's large storage is
    // complete; failing it would request the very same range again for ever.
    [Test]
    public void AddStorageRange_ProofOfAbsencePastTheOrigin_CompletesTheLargeStorage()
    {
        (PathWithAccount account, PathWithStorageSlot[] slots, StateTree stateTree) = BuildAccountWithStorage(100);
        ValueHash256 origin = slots[^1].Path.IncrementPath();

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        Assert.That(AnswerStorageContinuationWithProofOnly(snapProvider, progressTracker, account, origin, ProveStorage(stateTree, account.Path, origin)),
            Is.EqualTo(AddRangeResult.OK));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(progressTracker.LargeStorageProgressCount, Is.Zero, "the account's large storage is complete");
            Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True, "nothing is queued again for the account");
        }
    }

    // A proof that does not prove the rest of the range empty must fail the range, and the range must be retried from
    // its origin rather than have the account's storage restarted from 0. The failure is the serving peer's, not a sign
    // that the account moved, so the range goes back to the storage queue instead of re-proving the account.
    [TestCase(false, TestName = "{m} (junk proof)")]
    [TestCase(true, TestName = "{m} (real proof, slots past the origin withheld)")]
    public void AddStorageRange_ProofWithoutSlotLists_IsScoredAsAFailedRangeAndKeepsTheOrigin(bool realProof)
    {
        const int slotCount = 100;
        (PathWithAccount account, PathWithStorageSlot[] slots, StateTree stateTree) = BuildAccountWithStorage(slotCount);
        ValueHash256 origin = slots[slotCount / 2].Path;
        byte[][] proof = realProof ? ProveStorage(stateTree, account.Path, origin) : [[0x0]];

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        Assert.That(AnswerStorageContinuationWithProofOnly(snapProvider, progressTracker, account, origin, proof), Is.Not.EqualTo(AddRangeResult.OK));

        progressTracker.IsFinished(out SnapSyncBatch? retry);
        using (retry)
        {
#pragma warning disable NUnit2045 // The nullable value must be checked before accessing its contents.
            Assert.That(retry?.StorageRangeRequest, Is.Not.Null);
#pragma warning restore NUnit2045
            Assert.That(retry!.StorageRangeRequest!.Accounts[0].Path, Is.EqualTo(account.Path));
            Assert.That(retry.StorageRangeRequest.StartingHash, Is.EqualTo(origin), "the range is retried from its origin, not restarted");
        }
    }

    // DequeStorageToRetrieveRequest builds a batch from whatever it dequeues, so a batch can carry no account. A
    // proof-only reply to it is not a proof of absence for any account and must not be read as one for account 0.
    [Test]
    public void AddStorageRange_ProofOnlyReplyToABatchWithoutAccounts_IsOutOfBounds()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        using StorageRange request = CreateStorageRange(0);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(0),
            Proofs = new ByteArrayListAdapter(new List<byte[]> { new byte[] { 0x0 } }.ToPooledList()),
        };

#pragma warning disable NUnit2045 // Keep the state-changing response or range admission separate from dependent state checks.
        Assert.That(snapProvider.AddStorageRange(request, response), Is.EqualTo(AddRangeResult.OutOfBounds));
#pragma warning restore NUnit2045
        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True, "an empty batch queues nothing again");
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

        PathWithAccount account = new(TestItem.ValueKeccaks[0], AccountStruct.TotallyEmpty);
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

        PathWithAccount covered = new(TestItem.ValueKeccaks[0], AccountStruct.TotallyEmpty);
        PathWithAccount uncovered = new(TestItem.ValueKeccaks[1], AccountStruct.TotallyEmpty);
        progressTracker.EnqueueAccountStorage(covered);
        progressTracker.EnqueueAccountStorage(uncovered);
        DrainAccountRangePartition(progressTracker);

        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch!.StorageRangeRequest!.Accounts.Count, Is.EqualTo(2));
            batch.StorageRangeResponse = CreateEmptySlotsResponse(1);

            snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse);
            snapProvider.ReleaseRequest(batch, responseHandled: true);
        }

        // The covered account failed verification and goes for a refresh; the uncovered one comes back.
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
            progressTracker.ReportAccountRefreshFinished();
        }

        progressTracker.IsFinished(out SnapSyncBatch? retried);
        using (retried)
        {
            Assert.That(retried!.StorageRangeRequest!.Accounts.AsSpan()[0].Path, Is.EqualTo(uncovered.Path));
            progressTracker.ReportStorageRequestFinished(retried.StorageRangeRequest.Accounts.Count);
        }

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True);
    }

    // Regression for #13155: a storage range that keeps coming back empty (no slots, no proof) was re-queued at the
    // next pivot forever. A geth peer answers exactly like that for an account that has no storage at the requested
    // root, so the same account was asked for at every new pivot with no way out. After a streak that outlives the
    // feed's own pivot refreshes the account must be re-proven at the current pivot instead of being re-requested.
    [TestCase(1)]
    [TestCase(3)]
    public void AddStorageRange_EmptyResponseStreak_HandsTheAccountsToRefreshInsteadOfRetryingForever(int accountCount)
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount[] accounts = new PathWithAccount[accountCount];
        for (int i = 0; i < accountCount; i++)
        {
            accounts[i] = new PathWithAccount(TestItem.ValueKeccaks[i], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject.ToStruct());
            progressTracker.EnqueueAccountStorage(accounts[i]);
        }

        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? batch);
            using (batch)
            {
#pragma warning disable NUnit2045 // The nullable value must be checked before accessing its contents.
                Assert.That(batch!.StorageRangeRequest, Is.Not.Null, $"attempt {attempt}: below the streak limit the range is simply offered again");
#pragma warning restore NUnit2045
                Assert.That(batch.StorageRangeRequest!.Accounts.Count, Is.EqualTo(accountCount));

                batch.StorageRangeResponse = CreateEmptySlotsResponse(0);
                Assert.That(snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse), Is.EqualTo(AddRangeResult.ExpiredRootHash));
                snapProvider.ReleaseRequest(batch, responseHandled: true);
            }
        }

        for (int i = 0; i < accountCount; i++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? next);
            using (next)
            {
#pragma warning disable NUnit2045 // The nullable value must be checked before accessing its contents.
                Assert.That(next!.AccountsToRefreshRequest, Is.Not.Null,
                    "after the streak the account must be re-proven at the current pivot, not requested as a storage range again");
#pragma warning restore NUnit2045
                Assert.That(accounts.Select(static a => a.Path), Does.Contain(next.AccountsToRefreshRequest!.Paths[0].PathAndAccount!.Path));
                progressTracker.ReportAccountRefreshFinished();
            }
        }

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True, "no storage range may be left queued behind the refreshes");
    }

    // The counter only ever rises, so a promotion has to put it back: otherwise the account stays past the threshold
    // for good and, once a refresh has put its storage back on the queue, every single empty response promotes it
    // again - one refresh per response instead of one per streak, which is what fills the refresh queue.
    [Test]
    public void AddStorageRange_EmptyResponseStreak_StartsOverAfterEachPromotion()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject.ToStruct());
        progressTracker.EnqueueAccountStorage(account);
        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            AnswerNextStorageRangeWithAnEmptyResponse(snapProvider, progressTracker);
        }

        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null, "the streak promotes the account once");
            progressTracker.ReportAccountRefreshFinished();
        }

        // A refresh that finds the account still has storage puts its range back on the queue.
        progressTracker.EnqueueAccountStorage(account);
        AnswerNextStorageRangeWithAnEmptyResponse(snapProvider, progressTracker);

        Assert.That(progressTracker.AccountsToRefreshCount, Is.Zero,
            "one empty response after a promotion must not promote the account again - a fresh streak has to build up first");

        progressTracker.IsFinished(out SnapSyncBatch? retried);
        using (retried)
        {
            Assert.That(retried!.StorageRangeRequest, Is.Not.Null, "the range goes back to the ordinary storage queue");
        }
    }

    // A single-account range is not necessarily a large-storage continuation: DequeStorageToRetrieveRequest builds
    // its batch with StartingHash = Zero, so the tail of the storage queue holding one entry lands on the same
    // branch. Only the continuation carries a non-zero origin, and only it deserves the default-on Warn - an
    // ordinary account whose storage is empty at the pivot must not put one in an operator's log.
    [TestCase(false, TestName = "One-account batch with a zero origin stays on the Debug lane")]
    [TestCase(true, TestName = "Large-storage continuation warns")]
    public void AddStorageRange_EmptyResponseStreak_OnlyWarnsForALargeStorageContinuation(bool isContinuation)
    {
        LaneRecordingLogManager logManager = new();

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .AddSingleton<ILogManager>(logManager) // Put last or it wont work.
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        PathWithAccount account = new(TestItem.ValueKeccaks[0], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject.ToStruct());

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            using StorageRange request = new()
            {
                BlockNumber = 1,
                RootHash = Keccak.EmptyTreeHash,
                Accounts = new[] { account }.ToPooledList(1),
                StartingHash = isContinuation ? TestItem.ValueKeccaks[1] : ValueKeccak.Zero,
                LimitHash = isContinuation ? Keccak.MaxValue.ValueHash256 : null,
            };

            using SlotsAndProofs response = CreateEmptySlotsResponse(0);
            Assert.That(snapProvider.AddStorageRange(request, response), Is.EqualTo(AddRangeResult.ExpiredRootHash));
        }

        string[] promotions = logManager.Warns.Concat(logManager.Debugs)
            .Where(static line => line.Contains("came back empty"))
            .ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(promotions, Has.Length.EqualTo(1), "the streak must promote the account exactly once");
            Assert.That(logManager.Warns.Any(static line => line.Contains("came back empty")), Is.EqualTo(isContinuation),
                isContinuation
                    ? "a stalled continuation is unreachable by any other request and is worth an operator warning"
                    : "a one-account batch is an ordinary account and must not warn");
        }
    }

    private sealed class LaneRecordingLogManager : ILogManager, InterfaceLogger
    {
        public List<string> Warns { get; } = [];
        public List<string> Debugs { get; } = [];

        public ILogger GetClassLogger<T>() => new(this);
        public ILogger GetLogger(string loggerName) => new(this);

        public void Warn(string text) => Warns.Add(text);
        public void Debug(string text) => Debugs.Add(text);
        public void Info(string text) { }
        public void Trace(string text) { }
        public void Error(string text, Exception? ex = null) { }

        public bool IsWarn => true;
        public bool IsDebug => true;
        public bool IsInfo => false;
        public bool IsTrace => false;
        public bool IsError => false;
    }

    /// <summary>
    /// Registers one account as large storage, split over <paramref name="partitionCount"/> partitions the way
    /// <c>Sync.EnableSnapSyncStorageRangeSplit</c> does. Dequeuing a single-account slot range is what registers a
    /// partition, and each distinct limit hash counts as one.
    /// </summary>
    private static void RegisterLargeStorage(SnapProvider snapProvider, ProgressTracker progressTracker, PathWithAccount account, int partitionCount)
    {
        for (int i = 0; i < partitionCount; i++)
        {
            progressTracker.EnqueueNextSlot(new StorageRange
            {
                Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
                StartingHash = ValueKeccak.Zero.IncrementPath(),
                LimitHash = i == partitionCount - 1 ? Keccak.MaxValue : TestItem.Keccaks[i]
            });

            progressTracker.IsFinished(out SnapSyncBatch? slot);
            using (slot)
            {
                Assert.That(slot!.StorageRangeRequest, Is.Not.Null);
                snapProvider.ReleaseRequest(slot, responseHandled: true);
            }
        }

        Assert.That(progressTracker.LargeStorageProgressCount, Is.EqualTo(1), "guards the premise");
    }

    private static void AnswerNextStorageRangeWithAnEmptyResponse(SnapProvider snapProvider, ProgressTracker progressTracker)
    {
        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch!.StorageRangeRequest, Is.Not.Null);

            batch.StorageRangeResponse = CreateEmptySlotsResponse(0);
            Assert.That(snapProvider.AddStorageRange(batch.StorageRangeRequest!, batch.StorageRangeResponse), Is.EqualTo(AddRangeResult.ExpiredRootHash));
            snapProvider.ReleaseRequest(batch, responseHandled: true);
        }
    }

    // The promotion above is speculative: an empty response is also what a peer that has fallen behind the pivot
    // sends, and under a genuinely stale root every storage response is empty, so one response can push a whole
    // STORAGE_BATCH_SIZE batch over the streak limit at once. ProgressTracker.IsFinished serves the refresh queue
    // ahead of every other request type and a refresh that answers Expired re-queues itself, so an unbounded
    // promotion would leave no slot for account, storage or code work until the pivot moved. Past the cap the
    // account has to go back to the ordinary storage queue instead.
    [Test]
    public void AddStorageRange_EmptyResponseStreak_DoesNotQueueMoreRefreshesThanTheCap()
    {
        const int accountCount = SnapProvider.MaxQueuedEmptyStreakRefreshes + 6;

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        for (int i = 0; i < accountCount; i++)
        {
            progressTracker.EnqueueAccountStorage(new PathWithAccount(TestItem.ValueKeccaks[i], Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject.ToStruct()));
        }

        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses; attempt++)
        {
            progressTracker.IsFinished(out SnapSyncBatch? batch);
            using (batch)
            {
#pragma warning disable NUnit2045 // The nullable value must be checked before accessing its contents.
                Assert.That(batch!.StorageRangeRequest, Is.Not.Null, $"attempt {attempt}: the whole batch is offered again");
#pragma warning restore NUnit2045
                Assert.That(batch.StorageRangeRequest!.Accounts.Count, Is.EqualTo(accountCount));

                batch.StorageRangeResponse = CreateEmptySlotsResponse(0);
                Assert.That(snapProvider.AddStorageRange(batch.StorageRangeRequest, batch.StorageRangeResponse), Is.EqualTo(AddRangeResult.ExpiredRootHash));
                snapProvider.ReleaseRequest(batch, responseHandled: true);
            }
        }

        Assert.That(progressTracker.AccountsToRefreshCount, Is.EqualTo(SnapProvider.MaxQueuedEmptyStreakRefreshes),
            "the refresh queue must not grow past the cap, whatever the batch size");

        const int cap = SnapProvider.MaxQueuedEmptyStreakRefreshes;
        progressTracker.EnqueueCodeHash(TestItem.KeccakA);
        int refreshes = 0;
        int storageAccounts = 0;
        int refreshesBeforeStorageGotATurn = -1;
        int refreshesBeforeCodeGotATurn = -1;
        int mostSlotsInUse = 0;
        List<(int Accounts, int PendingRefreshes, int Slots)> storageTurns = [];

        for (int guard = 0; guard <= accountCount + 1; guard++)
        {
            if (progressTracker.IsFinished(out SnapSyncBatch? next)) break;

            mostSlotsInUse = Math.Max(mostSlotsInUse, progressTracker.Recovery.CohortSlots);
            using (next)
            {
                Assert.That(next, Is.Not.Null, "the queues are not empty yet");
                if (next!.AccountsToRefreshRequest is not null)
                {
                    refreshes++;
                    progressTracker.ReportAccountRefreshFinished();
                }
                else if (next.CodesRequest is not null)
                {
                    if (refreshesBeforeCodeGotATurn < 0) refreshesBeforeCodeGotATurn = refreshes;
                    progressTracker.ReportCodeRequestFinished(ReadOnlySpan<ValueHash256>.Empty);
                }
                else
                {
                    if (refreshesBeforeStorageGotATurn < 0) refreshesBeforeStorageGotATurn = refreshes;
                    int accounts = next.StorageRangeRequest!.Accounts.Count;
                    storageTurns.Add((accounts, cap - refreshes, progressTracker.Recovery.CohortSlots));
                    storageAccounts += accounts;
                    snapProvider.ReleaseRequest(next, responseHandled: true);
                }
            }
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(refreshes, Is.EqualTo(cap), "every promoted account is refreshed");
            Assert.That(storageAccounts, Is.EqualTo(6), "and the ones the cap turned away are back on the storage queue");
            Assert.That(mostSlotsInUse, Is.LessThanOrEqualTo(cap), "refreshes and overflow together never hold more than the cap");
            Assert.That(refreshesBeforeCodeGotATurn, Is.InRange(0, ProgressTracker.MAX_CONSECUTIVE_ACCOUNT_REFRESHES),
                "eligible regular code work gets a turn within four refreshes");
            Assert.That(refreshesBeforeStorageGotATurn, Is.GreaterThanOrEqualTo(0), "the unknown overflow gets a turn");
            foreach ((int accounts, int pendingRefreshes, int slots) in storageTurns)
            {
                // The promoted refreshes keep their slots until they finish, so the overflow stays queued until
                // enough of them have released theirs for its whole batch to fit under the cap.
                Assert.That(slots, Is.GreaterThanOrEqualTo(pendingRefreshes + accounts), "pending refreshes keep their slots");
                Assert.That(slots, Is.LessThanOrEqualTo(cap), "the overflow waits for free slots");
            }
            Assert.That(progressTracker.Recovery.CohortSlots, Is.Zero);
        }
    }

    // Regression for #13155, second half: when the re-proven account turns out to have no storage at the pivot any
    // more, there is nothing to fetch. Queueing its storage again would only draw the same empty responses.
    [Test]
    public void RefreshAccounts_AccountNoLongerHasStorage_DropsTheStorageRange()
    {
        (ISnapStateServer server, Hash256 root) = BuildSnapServerFromEntries(
        [
            (TestItem.KeccakA, Build.An.Account.WithBalance(1).TestObject),
            (TestItem.KeccakB, Build.An.Account.WithBalance(2).TestObject),
            (TestItem.KeccakC, Build.An.Account.WithBalance(3).TestObject),
        ]);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        // Discovered at an older pivot with storage; the storage has since been emptied.
        PathWithAccount stale = new(TestItem.KeccakA, Build.An.Account.WithStorageRoot(TestItem.KeccakF).TestObject.ToStruct());
        progressTracker.EnqueueAccountRefresh(stale, ValueKeccak.Zero, Keccak.MaxValue);

        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
#pragma warning disable NUnit2045 // The nullable value must be checked before accessing its contents.
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);
#pragma warning restore NUnit2045
            Assert.That(refresh.AccountsToRefreshRequest!.RootHash, Is.EqualTo(root));

            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(root, stale.Path, stale.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(stale.Account!.StorageRoot, Is.EqualTo(TestItem.KeccakF), "accepted account leaves remain immutable after a verified refresh");
            Assert.That(progressTracker.IsFinished(out SnapSyncBatch? next), Is.True, "an account without storage leaves nothing to fetch");
            Assert.That(next, Is.Null);
        }
    }

    /// <summary>
    /// The sibling of the test above. A large-storage account that is <em>deleted</em> at the pivot, rather than
    /// merely emptied, is just as terminal: nothing will ever fetch its storage, so nothing else will ever clear its
    /// large-storage entry. Leaving it counts the account in "Large storage left" for the rest of the sync, which is
    /// the exact operator signal #13155 was diagnosed by. Retiring one partition is not enough: under
    /// <c>Sync.EnableSnapSyncStorageRangeSplit</c> the account registers one per split, so the entry has to be dropped
    /// outright.
    /// </summary>
    [TestCase(1, TestName = "{m} (one partition)")]
    [TestCase(2, TestName = "{m} (split into two partitions)")]
    public void RefreshAccounts_AccountNoLongerExists_ClearsItsLargeStorageProgress(int partitionCount)
    {
        (ISnapStateServer server, Hash256 root) = BuildSnapServerFromEntries(
        [
            (TestItem.KeccakA, Build.An.Account.WithBalance(1).TestObject),
            (TestItem.KeccakB, Build.An.Account.WithBalance(2).TestObject),
        ]);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        // A path immediately before an existing account: absent from the state, and its neighbour is what proves
        // the absence. A gap with no successor would come back as an empty range, i.e. Expired rather than NotFound.
        ValueHash256 missing = ((ValueHash256)TestItem.KeccakB).DecrementPath();
        PathWithAccount gone = new(missing, Build.An.Account.WithStorageRoot(TestItem.KeccakG).TestObject.ToStruct());

        RegisterLargeStorage(snapProvider, progressTracker, gone, partitionCount);

        progressTracker.EnqueueAccountRefresh(gone, ValueKeccak.Zero, Keccak.MaxValue);
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);

            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(root, gone.Path, gone.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(gone.Account.StorageRoot, Is.EqualTo(TestItem.KeccakG), "an absent account adopts nothing; guards that this was NotFound and not Verified");
            Assert.That(progressTracker.LargeStorageProgressCount, Is.Zero, "a deleted account must not keep counting as large storage left");
        }
    }

    /// <summary>
    /// The third terminal-for-large-storage outcome. An account that still has storage, but whose refresh resumes it
    /// from origin 0, goes back to the multi-account batching queue - where nothing will ever call
    /// <see cref="ProgressTracker.OnCompletedLargeStorage"/> for it, because that only fires for a single-account
    /// slot range. Its old large-storage entry is obsolete the moment the account restarts at 0, so it has to be
    /// cleared here or it counts towards "Large storage left" for the rest of the sync - however many partitions the
    /// account had been split into.
    /// </summary>
    [TestCase(1, TestName = "{m} (one partition)")]
    [TestCase(2, TestName = "{m} (split into two partitions)")]
    public void RefreshAccounts_AccountRestartsFromOrigin_ClearsItsStaleLargeStorageProgress(int partitionCount)
    {
        Account withStorage = Build.An.Account.WithBalance(1).WithStorageRoot(TestItem.KeccakF).TestObject;
        (ISnapStateServer server, Hash256 root) = BuildSnapServerFromEntries(
        [
            (TestItem.KeccakA, withStorage),
            (TestItem.KeccakB, Build.An.Account.WithBalance(2).TestObject),
        ]);

        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(root)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        DrainAccountRangePartition(progressTracker);

        PathWithAccount account = new(TestItem.KeccakA, withStorage.ToStruct());

        RegisterLargeStorage(snapProvider, progressTracker, account, partitionCount);

        // Refreshed at origin 0, so the Verified arm re-enqueues the whole account rather than a continuation.
        progressTracker.EnqueueAccountRefresh(account, ValueKeccak.Zero, Keccak.MaxValue);
        progressTracker.IsFinished(out SnapSyncBatch? refresh);
        using (refresh)
        {
            Assert.That(refresh!.AccountsToRefreshRequest, Is.Not.Null);

            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(root, account.Path, account.Path.IncrementPath(), 4000, CancellationToken.None);
            refresh.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };

            Assert.That(snapProvider.RefreshAccounts(refresh.AccountsToRefreshRequest, refresh.AccountsToRefreshResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(refresh, responseHandled: true);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Account.StorageRoot, Is.EqualTo(TestItem.KeccakF), "guards that this was Verified with storage, not NotFound or emptied");
            Assert.That(progressTracker.LargeStorageProgressCount, Is.Zero,
                "an account restarted at origin 0 must not keep its stale large-storage entry");
        }

        progressTracker.IsFinished(out SnapSyncBatch? requeued);
        using (requeued)
        {
            Assert.That(requeued!.StorageRangeRequest, Is.Not.Null, "and it must still be queued for its storage");
        }
    }

    // The streak counter has to be cleared by a SERVED response too, not only by a promotion: otherwise an account
    // that draws a few empties, is then served normally, and later draws a few more is promoted on a streak that was
    // never consecutive.
    [Test]
    public void AddStorageRange_ServedResponse_EndsTheEmptyStreak()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 })
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        using TestMemDb db = new();
        TestRawTrieStore store = new(db);
        StorageTree storage = new(store.GetTrieStore(TestItem.ValueKeccaks[0].ToCommitment()), LimboLogs.Instance);
        storage.Set(ValueKeccak.Zero, [1], false);
        storage.Commit();
        PathWithAccount account = new(TestItem.ValueKeccaks[0], Build.An.Account.WithStorageRoot(storage.RootHash).TestObject.ToStruct());
        progressTracker.EnqueueAccountStorage(account);
        DrainAccountRangePartition(progressTracker);

        for (int attempt = 0; attempt < SnapProvider.MaxConsecutiveEmptyStorageResponses - 1; attempt++)
        {
            AnswerNextStorageRangeWithAnEmptyResponse(snapProvider, progressTracker);
        }

        Assert.That(account.EmptyStorageResponses, Is.EqualTo(SnapProvider.MaxConsecutiveEmptyStorageResponses - 1), "guards the premise");

        progressTracker.IsFinished(out SnapSyncBatch? served);
        using (served)
        {
            Assert.That(served!.StorageRangeRequest, Is.Not.Null);
            served.StorageRangeResponse = new()
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1)
                {
                    new ArrayPoolList<PathWithStorageSlot>(1) { new(ValueKeccak.Zero, new byte[] { 1 }) }
                }
            };
            Assert.That(snapProvider.AddStorageRange(served.StorageRangeRequest!, served.StorageRangeResponse), Is.EqualTo(AddRangeResult.OK));
            snapProvider.ReleaseRequest(served, responseHandled: true);
        }

        Assert.That(account.EmptyStorageResponses, Is.Zero, "a served response means the peers are answering this account again");
    }

    [TestCase(nameof(SnapSyncBatch.AccountRangeRequest))]
    [TestCase(nameof(SnapSyncBatch.StorageRangeRequest))]
    [TestCase(nameof(SnapSyncBatch.CodesRequest))]
    public void HandleResponse_ProcessingThrows_OffersTheRequestAgain(string requestKind)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        peerPool.InitializedPeers.Returns([new PeerInfo(syncPeer)]);
        using IContainer container = CreateContainerBuilder(
                new TestSyncConfig { SnapSyncAccountRangePartitionCount = 1 },
                (_, _) => new TestSnapTrieFactory(
                    static () => throw new IOException("state backend unavailable"),
                    static () => throw new IOException("state backend unavailable")))
            .WithSuggestedHeaderOfStateRoot(Keccak.EmptyTreeHash)
            .AddSingleton(peerPool)
            .Build();

        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();
        ISimpleSyncFeed<SnapSyncBatch> feed = container.Resolve<ISimpleSyncFeed<SnapSyncBatch>>();

        ValueHash256 work = TestItem.ValueKeccaks[0];
        switch (requestKind)
        {
            case nameof(SnapSyncBatch.StorageRangeRequest):
                progressTracker.EnqueueAccountStorage(new(work, AccountStruct.TotallyEmpty));
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
        using (retried)
        {
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
        }

        Assert.That(progressTracker.IsSnapGetRangesFinished(), Is.True,
            "the work was queued again but its active request count was never released");
    }

    private static void AttachThrowingResponse(SnapSyncBatch batch)
    {
        if (batch.AccountRangeRequest is not null)
        {
            batch.AccountRangeResponse = new AccountsAndProofs
            {
                PathAndAccounts = new ArrayPoolList<PathWithAccount>(1) { new(TestItem.ValueKeccaks[0], AccountStruct.TotallyEmpty) },
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
        (PathWithAccount account, PathWithStorageSlot[] slots, StateTree stateTree) = BuildAccountWithStorage(6);

        using IContainer container = CreateContainer();
        SnapProvider snapProvider = container.Resolve<SnapProvider>();

        StorageRange storageRange = new()
        {
            StartingHash = Keccak.Zero,
            Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
        };

        Assert.That(snapProvider.AddStorageRangeForAccount(
            storageRange, 0, slots,
            new ByteArrayListAdapter(ProveStorage(stateTree, account.Path, Keccak.Zero, slots[^1].Path).ToPooledList())), Is.EqualTo(AddRangeResult.OK));
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
        using (Assert.EnterMultipleScope())
        {
            Assert.That(startingHash.CompareTo(entries[3].Item1), Is.GreaterThan(0));
            Assert.That(startingHash.CompareTo(entries[4].Item1), Is.LessThan(0));
        }
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

#pragma warning disable NUnit2045 // Keep the state-changing response or range admission separate from dependent state checks.
        Assert.That(snapProvider.AddAccountRange(batch?.AccountRangeRequest!, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));
#pragma warning restore NUnit2045

        Assert.That(container.ResolveNamed<IDb>(DbNames.State).GetAllKeys().Count(), Is.EqualTo(3)); // 3 child. Root branch node not saved due to state sync compatibility
    }

    [Test]
    public void Test_EdgeCases([Values("badreq-roothash.zip", "badreq-roothash-2.zip", "badreq-roothash-3.zip", "badreq-trieexception.zip")] string testFileName)
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
        List<PathWithAccount> pathWithAccounts = accounts.Select((acc, idx) => new PathWithAccount(paths[idx], acc.ToStruct())).ToList();
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

    public enum Interruption { Crash, CrashWithRequestInFlight, Shutdown }

    /// <summary>
    /// A restart keeps what the last checkpoint covers and downloads the rest again, now at a newer root. After a crash,
    /// what the interrupted run wrote after that checkpoint is still on disk, as when the crash flushed it, including
    /// what a request in flight at the checkpoint wrote once answered. A shutdown saves a checkpoint of everything
    /// written, counting the request it dropped in flight as still to do. Resuming has to leave exactly what a run that
    /// was never interrupted leaves when its pivot moves at that checkpoint, which is the state healing knows how to
    /// finish. The accounts past the checkpoint, and the storage requested in flight, change between the roots, so
    /// anything left over from the older root shows up as a difference, and the double-write check fails on any key
    /// written twice.
    /// </summary>
    [Test]
    public async Task Flat_snap_sync_resumed_from_a_checkpoint_matches_a_run_whose_pivot_moved_there([Values] Interruption interruption)
    {
        const int checkpointedRequests = 6;

        TestRawTrieStore sourceStore = new(new TestMemDb());
        StateTree sourceTree = new(sourceStore, LimboLogs.Instance);
        SnapStateServer server = new(sourceStore.AsReadOnly(), LimboLogs.Instance);
        SortedDictionary<ValueHash256, Account> accounts = [];
        Dictionary<ValueHash256, StorageTree> storages = [];
        for (int i = 0; i < 40; i++)
        {
            ValueHash256 path = Keccak.Compute(i.ToBigEndianByteArray());
            accounts[path] = Build.An.Account.WithBalance((UInt256)(i + 1)).TestObject;
            if (i % 3 != 0) continue;

            StorageTree storage = new(sourceStore.GetTrieStore(path.ToCommitment()), LimboLogs.Instance);
            for (int slot = 0; slot < 4; slot++) storage.Set(SlotKey(slot), [(byte)(slot + 1)]);
            storages[path] = storage;
        }

        Hash256 firstRoot = CommitSource(0);
        TestSyncConfig syncConfig = new() { SnapResume = true, EnableSnapDoubleWriteCheck = true, SnapSyncAccountRangePartitionCount = 1 };

        using SnapshotableMemColumnsDb<FlatDbColumns> resumedDb = new();
        FlatSnapTrieFactory resumedFactory = CreateFlatSnapTrieFactory(resumedDb, syncConfig);
        IStateSyncPivot resumedPivot = CreatePivot(1, firstRoot);
        byte[]? lastDurableCheckpoint;
        int movePivotAt = checkpointedRequests;
        ValueHash256? requestedInFlight = null;
        if (interruption == Interruption.Shutdown)
        {
            await RunSnapSync(resumedFactory, syncConfig, resumedPivot, server, shutDownAtRequest: checkpointedRequests);
            lastDurableCheckpoint = BasePersistence.ReadSnapCheckpoint(resumedDb.GetColumnDb(FlatDbColumns.Metadata));
        }
        else
        {
            // The crashed run went on to the end, but a checkpoint after a few requests is the last to reach the disk.
            List<byte[]?> checkpointsBeforeRequest = [];
            HeldRequests? held = await RunSnapSync(resumedFactory, syncConfig, resumedPivot, server,
                beforeRequest: _ => checkpointsBeforeRequest.Add(BasePersistence.ReadSnapCheckpoint(resumedDb.GetColumnDb(FlatDbColumns.Metadata))),
                holdInFlightFrom: interruption == Interruption.CrashWithRequestInFlight ? checkpointedRequests : null);

            // The checkpoint saved with request n is read before request n + 1. The one saved with the request after the
            // last one held was taken with them all in flight, so it covers only the requests before the first.
            int lastCovered = checkpointedRequests;
            if (interruption == Interruption.CrashWithRequestInFlight)
            {
                Assert.That(held, Is.Not.Null, "a storage request has to be held in flight");
                movePivotAt = held!.First;
                lastCovered = held.Last + 1;
                requestedInFlight = held.StorageAccount;
            }

            lastDurableCheckpoint = checkpointsBeforeRequest[lastCovered + 1];
            BasePersistence.WriteSnapCheckpoint(resumedDb, lastDurableCheckpoint);
        }

        ValueHash256 resumeFrom = SnapCheckpoint.Decode(lastDurableCheckpoint).Partitions[0].NextAccountPath;
        ValueHash256[] afterCheckpoint = accounts.Keys.Where(path => path >= resumeFrom).ToArray();
        Assert.That(afterCheckpoint, Has.Length.GreaterThan(4), "the checkpoint has to leave accounts to download again");

        ValueHash256 deleted = afterCheckpoint.First(path => !storages.ContainsKey(path));
        ValueHash256 withChangedStorage = afterCheckpoint.First(storages.ContainsKey);
        ValueHash256 withChangedBalance = afterCheckpoint.Last(path => path != deleted);
        accounts.Remove(deleted);
        sourceTree.Set(deleted, null);
        accounts[withChangedBalance] = accounts[withChangedBalance].WithChangedBalance(1000);
        storages[withChangedStorage].Set(SlotKey(0), [0], rlpEncode: false);
        storages[withChangedStorage].Set(SlotKey(1), [0x42]);
        if (requestedInFlight is { } inFlight) storages[inFlight].Set(SlotKey(2), [0x43]);
        Hash256 secondRoot = CommitSource(1);

        MovePivot(resumedPivot, 2, secondRoot);
        await RunSnapSync(resumedFactory, syncConfig, resumedPivot, server);

        using SnapshotableMemColumnsDb<FlatDbColumns> uninterruptedDb = new();
        IStateSyncPivot uninterruptedPivot = CreatePivot(1, firstRoot);
        await RunSnapSync(CreateFlatSnapTrieFactory(uninterruptedDb, syncConfig), syncConfig, uninterruptedPivot, server, beforeRequest: request =>
        {
            if (request == movePivotAt) MovePivot(uninterruptedPivot, 2, secondRoot);
        });

        using IPersistence.IPersistenceReader reader = new RocksDbPersistence(resumedDb, LimboLogs.Instance, NullTrieNodeLog.Instance).CreateReader();
        SnapCheckpoint? finished = BasePersistence.ReadSnapCheckpoint(resumedDb.GetColumnDb(FlatDbColumns.Metadata)) is { } record ? SnapCheckpoint.Decode(record) : null;
        using (Assert.EnterMultipleScope())
        {
            foreach (FlatDbColumns column in Enum.GetValues<FlatDbColumns>())
            {
                Assert.That(ReadColumn(resumedDb, column), Is.EqualTo(ReadColumn(uninterruptedDb, column)), $"{column} column");
            }

            Assert.That(resumedPivot.UpdatedStorages, Is.EquivalentTo(uninterruptedPivot.UpdatedStorages), "accounts to heal");
            Assert.That(finished?.Partitions.Select(static p => p.MoreAccountsToRight), Is.EqualTo(new[] { false }), "a finished state ranges phase leaves a checkpoint of finished ranges");
            Assert.That(finished?.StorageRangesToRefetch(), Is.Empty, "with no storage left to download");

            Assert.That(reader.GetAccountRaw(deleted), Is.Null, "the account deleted since the checkpoint");
            RlpReader changed = new(reader.GetAccountRaw(withChangedBalance));
            Assert.That(AccountDecoder.Slim.Decode(ref changed)!.Balance, Is.EqualTo(accounts[withChangedBalance].Balance), "the balance changed since the checkpoint");
            UInt256 value = default;
            Assert.That(reader.TryGetStorageRaw(withChangedStorage, SlotKey(0), ref value), Is.False, "the slot cleared since the checkpoint");
        }

        Hash256 CommitSource(ulong blockNumber)
        {
            using IBlockCommitter _ = sourceStore.BeginBlockCommit(blockNumber);
            foreach ((ValueHash256 path, StorageTree storage) in storages)
            {
                storage.Commit();
                if (accounts.TryGetValue(path, out Account? account)) accounts[path] = account.WithChangedStorageRoot(storage.RootHash);
            }

            foreach ((ValueHash256 path, Account account) in accounts) sourceTree.Set(path, account);
            sourceTree.Commit();
            return sourceTree.RootHash;
        }

        static ValueHash256 SlotKey(int slot) => Keccak.Compute(slot.ToBigEndianByteArray());
    }

    public enum HealingStop { DuringReassembly, AfterChunk, AfterRound, AwaitingFinalPivot }

    /// <summary>
    /// BAL healing writes skip the WAL, so the checkpoint of the finished ranges is deleted before healing writes
    /// anything, and a crash during healing starts the sync over. A graceful stop lands between the chunks healing writes,
    /// flushes them and saves the checkpoint again, so the restarted sync reassembles the trie over them and replays the
    /// BALs from the anchor again. That has to leave the flat state a run never stopped leaves, under a trie it verifies
    /// against. The BALs delete and revive accounts, clear and set slots and deploy code, in both rounds of healing.
    /// </summary>
    [Test]
    public async Task Flat_snap_sync_stopped_during_healing_resumes_to_the_state_of_a_run_never_stopped([Values] HealingStop stop, [Values] bool crash)
    {
        const int movePivotAt = 6;
        const int accountCount = 40;

        TestRawTrieStore sourceStore = new(new TestMemDb());
        StateTree sourceTree = new(sourceStore, LimboLogs.Instance);
        SnapStateServer server = new(sourceStore.AsReadOnly(), LimboLogs.Instance);
        Dictionary<Address, Account> accounts = [];
        Dictionary<Address, StorageTree> storages = [];
        for (int i = 0; i < accountCount; i++)
        {
            Address address = TestItem.Addresses[i];
            accounts[address] = Build.An.Account.WithBalance((UInt256)(i + 1)).TestObject;
            if (i % 3 != 0) continue;

            StorageTree storage = new(sourceStore.GetTrieStore(address.ToAccountPath.ToCommitment()), LimboLogs.Instance);
            for (int slot = 1; slot <= 4; slot++) storage.Set((UInt256)slot, [(byte)slot]);
            storages[address] = storage;
        }

        // Accounts 2 and 5 hold no storage, so an empty balance deletes them.
        byte[] code = [0x60, 0x00, 0x60, 0x01];
        Dictionary<ulong, List<ReadOnlyAccountChanges>> changes = [];
        Dictionary<ulong, Hash256> roots = new() { [1] = CommitSource(1) };
        SetBalance(2, 1, 1000);
        SetSlot(2, 0, 1, 0x42);
        SetBalance(2, 2, 0);
        roots[2] = CommitSource(2);
        SetSlot(5, 3, 2, 0);
        DeployCode(9, 4);
        SetBalance(19, 5, 0);
        roots[20] = CommitSource(20);
        SetBalance(25, 5, 77);
        SetSlot(30, 0, 3, 0x99);
        SetBalance(30, 2, 5);
        SetBalance(38, 1, 2000);
        roots[40] = CommitSource(40);

        Dictionary<ulong, BlockHeader> headers = [];
        BlockAccessListStore balStore = new(new MemDb());
        for (ulong number = 1; number <= 40; number++)
        {
            headers[number] = Build.A.BlockHeader.WithNumber(number).WithParentHash(headers.GetValueOrDefault(number - 1)?.Hash ?? Keccak.Zero)
                .WithStateRoot(roots.GetValueOrDefault(number) ?? Keccak.EmptyTreeHash).TestObject;
            if (number > 1)
                balStore.Insert(number, headers[number].Hash!, Build.A.BlockAccessList.WithAccountChanges([.. changes.GetValueOrDefault(number) ?? []]).TestObject);
        }

        IBlockTree blockTree = Substitute.For<IBlockTree>();
        blockTree.FindHeader(Arg.Any<ulong>()).Returns(call => headers.GetValueOrDefault(call.Arg<ulong>()));
        blockTree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
            .Returns(call => headers.Values.FirstOrDefault(header => header.Hash == call.Arg<Hash256>()));
        blockTree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
        ManualTimestamper timestamper = new(DateTimeOffset.FromUnixTimeSeconds((long)headers[1].Timestamp).UtcDateTime);
        TestSyncConfig syncConfig = new() { SnapResume = true, SnapSyncAccountRangePartitionCount = 1 };
        List<byte[]?> checkpointsWhileApplying = [];

        using SnapshotableMemColumnsDb<FlatDbColumns> resumedDb = new();
        using CancellationTokenSource shutdown = new();
        byte[]? rangesCheckpoint = null;
        HealingPivot stoppedPivot = new(headers[1], headers[20], headers[40]);
        StateSyncRunner stopped = StartNode(resumedDb, stoppedPivot, stopping: true);
        Exception? thrown = null;
        try
        {
            await stopped.RunSnapSyncWithBalHealing(headers[1], shutdown.Token);
        }
        catch (Exception e)
        {
            thrown = e;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(thrown, crash ? Is.Not.Null.And.Not.InstanceOf<OperationCanceledException>() : Is.InstanceOf<OperationCanceledException>());
            Assert.That(ReadCheckpoint(resumedDb), crash ? Is.Null : Is.EqualTo(rangesCheckpoint).And.Not.Null, "the checkpoint left by the stop");
        }

        StateSyncRunner resumed = StartNode(resumedDb, new HealingPivot(headers[40], headers[40]), stopping: false);
        BlockHeader? anchor = resumed.GetResumedAnchor();
        if (crash)
        {
            Assert.That(anchor, Is.Null, "nothing to resume after a crash");
            CreateFlatSnapTrieFactory(resumedDb, syncConfig).EnsureInitialize();
            Assert.That(resumedDb.GetColumnDb(FlatDbColumns.Account).GetAll(), Is.Empty, "the sync starts over");
            return;
        }

        Assert.That(anchor, Is.EqualTo(headers[1]), "the anchor resumed from");
        await resumed.RunSnapSyncWithBalHealing(anchor!, default);

        using SnapshotableMemColumnsDb<FlatDbColumns> uninterruptedDb = new();
        await StartNode(uninterruptedDb, new HealingPivot(headers[1], headers[20], headers[40]), stopping: false).RunSnapSyncWithBalHealing(headers[1], default);

        using IPersistence.IPersistenceReader reader = new RocksDbPersistence(resumedDb, LimboLogs.Instance, NullTrieNodeLog.Instance).CreateReader();
        TestErrorLogManager verifierErrors = new();
        FlatTrieVerifier verifier = new(verifierErrors);
        verifier.Verify(reader, new FlatLocalDbContext.ReadOnlyTrieStore(reader), roots[40], default);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(checkpointsWhileApplying, Is.Not.Empty.And.All.Null, "the checkpoint is gone while healing writes");
            Assert.That(ReadColumn(resumedDb, FlatDbColumns.Account), Is.EqualTo(ReadColumn(uninterruptedDb, FlatDbColumns.Account)), "account column");
            Assert.That(ReadColumn(resumedDb, FlatDbColumns.Storage), Is.EqualTo(ReadColumn(uninterruptedDb, FlatDbColumns.Storage)), "storage column");
            Assert.That(verifier.Stats, Is.EqualTo(new FlatTrieVerifier.VerificationStats(accounts.Count, verifier.Stats.SlotCount, 0, 0, 0, 0)), "flat against trie");
            Assert.That(verifierErrors.Errors, Is.Empty, "trie node hashes");
        }

        // One node's run: the ranges with the pivot moved at movePivotAt, or resumed already finished, then healing.
        StateSyncRunner StartNode(IColumnsDb<FlatDbColumns> db, HealingPivot pivot, bool stopping)
        {
            FlatSnapTrieFactory factory = CreateFlatSnapTrieFactory(db, syncConfig);
            ISnapSyncRunner snapSyncRunner = Substitute.For<ISnapSyncRunner>();
            snapSyncRunner.Run(Arg.Any<CancellationToken>()).Returns(async _ =>
            {
                await RunSnapSync(factory, syncConfig, pivot, server, beforeRequest: request =>
                {
                    if (request == movePivotAt) pivot.Header = headers[2];
                });
                rangesCheckpoint ??= ReadCheckpoint(db);
            });

            IBlockAccessListStore observedBals = Substitute.For<IBlockAccessListStore>();
            observedBals.Exists(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(call => balStore.Exists(call.ArgAt<ulong>(0), call.ArgAt<Hash256>(1)));
            observedBals.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(call =>
            {
                checkpointsWhileApplying.Add(ReadCheckpoint(db));
                // The second chunk of the first round, from the block after the anchor.
                if (call.ArgAt<ulong>(0) == headers[1].Number + 1 + FlatBalHealing.BalsChunkSize) StopIf(HealingStop.AfterChunk);
                return balStore.Get(call.ArgAt<ulong>(0), call.ArgAt<Hash256>(1));
            });

            RocksDbPersistence persistence = new(db, LimboLogs.Instance, NullTrieNodeLog.Instance);
            FlatBalHealing flatHealing = new(blockTree, observedBals, new TrieReassembler(persistence, LimboLogs.Instance), persistence,
                Substitute.For<ITreeSyncStore>(), new MemDb(), new Lazy<ICodeRecovery>(Substitute.For<ICodeRecovery>()), LimboLogs.Instance);
            IBalHealing healing = Substitute.For<IBalHealing>();
            healing.Reassemble(Arg.Any<IReadOnlyCollection<Hash256>>(), Arg.Any<CancellationToken>()).Returns(call =>
                flatHealing.Reassemble(new StopAfterStorageTries(call.Arg<IReadOnlyCollection<Hash256>>(), () => StopIf(HealingStop.DuringReassembly)), call.Arg<CancellationToken>()));
            healing.ApplyRange(Arg.Any<Hash256>(), Arg.Any<BlockHeader>(), Arg.Any<BlockHeader>(), Arg.Any<CancellationToken>()).Returns(call =>
                flatHealing.ApplyRange(call.ArgAt<Hash256>(0), call.ArgAt<BlockHeader>(1), call.ArgAt<BlockHeader>(2), call.ArgAt<CancellationToken>(3)));
            healing.TryRecoverMissingCode(Arg.Any<CancellationToken>()).Returns(call => flatHealing.TryRecoverMissingCode(call.Arg<CancellationToken>()));

            pivot.OnUpdate = round =>
            {
                if (round == 1) StopIf(HealingStop.AfterRound);
            };
            pivot.CanFinalizeAt = _ =>
            {
                StopIf(HealingStop.AwaitingFinalPivot);
                return !stopping || stop != HealingStop.AwaitingFinalPivot;
            };

            ISyncModeSelector syncModeSelector = Substitute.For<ISyncModeSelector>();
            syncModeSelector.WaitUntilMode(Arg.Any<Func<SyncMode, bool>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
            ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
            syncPeerPool.InitializedPeers.Returns([]);
            return new StateSyncRunner(snapSyncRunner, factory, healing, new StateHealingStrategy(syncConfig, new Lazy<IBalHealing>(healing), LimboLogs.Instance),
                new BalFetcher(syncPeerPool, blockTree, observedBals, syncConfig, LimboLogs.Instance), pivot, blockTree, null!, null!, syncConfig, syncModeSelector,
                Substitute.For<ISyncProgressResolver>(), Substitute.For<IBeaconSyncStrategy>(), syncPeerPool, timestamper, null, null, LimboLogs.Instance);

            void StopIf(HealingStop at)
            {
                if (!stopping || at != stop) return;
                if (crash) throw new InvalidOperationException("Crashed");
                shutdown.Cancel();
            }
        }

        void SetBalance(ulong block, int account, UInt256 balance)
        {
            Address address = TestItem.Addresses[account];
            if (balance.IsZero)
            {
                accounts.Remove(address);
                sourceTree.Set(address, null);
            }
            else
            {
                accounts[address] = accounts.TryGetValue(address, out Account? existing) ? existing.WithChangedBalance(balance) : Build.An.Account.WithBalance(balance).TestObject;
            }

            AddChange(block, Build.An.AccountChanges.WithAddress(address).WithBalanceChanges(new BalanceChange(0, balance)).TestObject);
        }

        void SetSlot(ulong block, int account, int slot, byte value)
        {
            Address address = TestItem.Addresses[account];
            storages[address].Set((UInt256)slot, [value]);
            AddChange(block, Build.An.AccountChanges.WithAddress(address).WithStorageChanges((UInt256)slot, new StorageChange(0, value)).TestObject);
        }

        void DeployCode(ulong block, int account)
        {
            Address address = TestItem.Addresses[account];
            accounts[address] = accounts[address].WithChangedNonce(1).WithChangedCodeHash(Keccak.Compute(code));
            AddChange(block, Build.An.AccountChanges.WithAddress(address)
                .WithNonceChanges(new NonceChange(0, 1)).WithCodeChanges(new CodeChange(0, code)).TestObject);
        }

        void AddChange(ulong block, ReadOnlyAccountChanges change)
        {
            if (!changes.TryGetValue(block, out List<ReadOnlyAccountChanges>? blockChanges)) changes[block] = blockChanges = [];
            blockChanges.Add(change);
        }

        Hash256 CommitSource(ulong blockNumber)
        {
            using IBlockCommitter _ = sourceStore.BeginBlockCommit(blockNumber);
            foreach ((Address address, StorageTree storage) in storages)
            {
                storage.Commit();
                if (accounts.TryGetValue(address, out Account? account)) accounts[address] = account.WithChangedStorageRoot(storage.RootHash);
            }

            foreach ((Address address, Account account) in accounts) sourceTree.Set(address, account);
            sourceTree.Commit();
            return sourceTree.RootHash;
        }

        static byte[]? ReadCheckpoint(IColumnsDb<FlatDbColumns> db) => BasePersistence.ReadSnapCheckpoint(db.GetColumnDb(FlatDbColumns.Metadata));
    }

    private static SortedDictionary<string, string> ReadColumn(IColumnsDb<FlatDbColumns> db, FlatDbColumns column) =>
        [with(db.GetColumnDb(column).GetAll().ToDictionary(static kv => Convert.ToHexString(kv.Key), static kv => Convert.ToHexString(kv.Value)))];

    /// <summary>A pivot at <see cref="Header"/>, which each healing round moves to the next of <paramref name="rounds"/>, staying at the last.</summary>
    private sealed class HealingPivot(BlockHeader header, params BlockHeader[] rounds) : IStateSyncPivot
    {
        private int _round;

        public BlockHeader Header { get; set; } = header;

        /// <summary>Called with the index of the round about to start.</summary>
        public Action<int>? OnUpdate { get; set; }

        public Func<BlockHeader, bool> CanFinalizeAt { get; set; } = static _ => true;

        public ConcurrentHashSet<Hash256> UpdatedStorages { get; } = [];

        public ulong Diff => 0;

        public BlockHeader GetPivotHeader() => Header;

        public void UpdateHeaderForcefully()
        {
            OnUpdate?.Invoke(_round);
            Header = rounds[Math.Min(_round++, rounds.Length - 1)];
        }

        public bool CanFinalize(BlockHeader pivot) => CanFinalizeAt(pivot);
    }

    /// <summary>Calls <paramref name="stop"/> once trie reassembly has gone through the accounts to heal, rebuilding their storage tries, and before it rebuilds the state trie.</summary>
    private sealed class StopAfterStorageTries(IReadOnlyCollection<Hash256> accounts, Action stop) : IReadOnlyCollection<Hash256>
    {
        public int Count => accounts.Count;

        public IEnumerator<Hash256> GetEnumerator()
        {
            foreach (Hash256 account in accounts) yield return account;
            stop();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static FlatSnapTrieFactory CreateFlatSnapTrieFactory(IColumnsDb<FlatDbColumns> db, ISyncConfig syncConfig) =>
        new(new RocksDbPersistence(db, LimboLogs.Instance, NullTrieNodeLog.Instance), Substitute.For<IPersistenceManager>(), db, new MemDb(), syncConfig, LimboLogs.Instance);

    private static IStateSyncPivot CreatePivot(ulong blockNumber, Hash256 stateRoot)
    {
        IStateSyncPivot pivot = Substitute.For<IStateSyncPivot>();
        ConcurrentHashSet<Hash256> updatedStorages = [];
        pivot.UpdatedStorages.Returns(updatedStorages);
        MovePivot(pivot, blockNumber, stateRoot);
        return pivot;
    }

    private static void MovePivot(IStateSyncPivot pivot, ulong blockNumber, Hash256 stateRoot) =>
        pivot.GetPivotHeader().Returns(Build.A.BlockHeader.WithNumber(blockNumber).WithStateRoot(stateRoot).TestObject);

    /// <summary>Runs a snap sync state ranges phase to the end, answering each request from <paramref name="server"/> as it is made.</summary>
    /// <param name="beforeRequest">
    /// Called with the number of each request before it is made. With a checkpoint before every request, the one saved
    /// before request <c>n</c> covers the first <c>n</c> requests.
    /// </param>
    /// <param name="shutDownAtRequest">
    /// Shuts down once this request is made, leaving it unanswered, as a node shutting down drops it. No checkpoint is
    /// taken while syncing then, so the one saved at the shutdown is the only one.
    /// </param>
    /// <param name="holdInFlightFrom">
    /// Leaves the requests made from this one on unanswered up to the first storage request, holding them in flight
    /// while the next request is made and answered, so the checkpoint taken with that one has them all in flight. Only
    /// then are they answered. An account range held in flight is what lets a storage request go out before the account
    /// ranges are done.
    /// </param>
    /// <returns>The requests held in flight, if any were.</returns>
    private static async Task<HeldRequests?> RunSnapSync(ISnapTrieFactory factory, ISyncConfig syncConfig, IStateSyncPivot pivot, ISnapStateServer server, Action<int>? beforeRequest = null, int? shutDownAtRequest = null, int? holdInFlightFrom = null)
    {
        // Small enough that a range takes several requests.
        const long byteLimit = 250;

        using ProgressTracker tracker = new(factory, syncConfig, pivot, LimboLogs.Instance,
            checkpointInterval: shutDownAtRequest is null ? TimeSpan.Zero : TimeSpan.MaxValue);
        SnapProvider provider = new(tracker, new MemDb(), factory, LimboLogs.Instance);
        using CancellationTokenSource shutdown = new();
        HeldRequests? held = null;
        SnapSyncRunner runner = new(_ =>
        {
            List<SnapSyncBatch> inFlight = [];
            int? firstHeld = null;
            for (int request = 0; ; request++)
            {
                beforeRequest?.Invoke(request);
                if (tracker.IsFinished(out SnapSyncBatch? batch)) break;

                if (request == shutDownAtRequest)
                {
                    shutdown.Cancel();
                    break;
                }

                if (held is null && request >= holdInFlightFrom && batch is not null)
                {
                    firstHeld ??= request;
                    inFlight.Add(batch);
                    if (batch.StorageRangeRequest is { } storageRange) held = new HeldRequests(firstHeld.Value, request, storageRange.Accounts[0].Path);
                    continue;
                }

                // Nothing else is left to request while the held ones are in flight.
                if (batch is not null) Answer(batch);
                else if (inFlight.Count == 0) throw new InvalidOperationException("No request to answer.");

                foreach (SnapSyncBatch answeredLate in inFlight) Answer(answeredLate);
                inFlight.Clear();
                // Released before a storage request went out, so holding starts over at the next request.
                if (held is null) firstHeld = null;
            }

            return Task.CompletedTask;
        }, factory, tracker);

        await runner.Run(shutdown.Token);
        return held;

        void Answer(SnapSyncBatch batch)
        {
            using (batch)
            {
                AnswerFrom(server, provider, batch, byteLimit);
                provider.ReleaseRequest(batch, responseHandled: true);
            }
        }
    }

    /// <param name="First">The number of the first request held in flight.</param>
    /// <param name="Last">The number of the last request held, the storage request.</param>
    /// <param name="StorageAccount">The first account of the storage request held.</param>
    private sealed record HeldRequests(int First, int Last, ValueHash256 StorageAccount);

    private static void AnswerFrom(ISnapStateServer server, SnapProvider provider, SnapSyncBatch batch, long byteLimit)
    {
        if (batch.AccountRangeRequest is { } accountRange)
        {
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(accountRange.RootHash, accountRange.StartingHash, accountRange.LimitHash, byteLimit, CancellationToken.None);
            batch.AccountRangeResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };
            Assert.That(provider.AddAccountRange(accountRange, batch.AccountRangeResponse), Is.EqualTo(AddRangeResult.OK));
        }
        else if (batch.StorageRangeRequest is { } storageRange)
        {
            (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> slots, IByteArrayList? proofs) =
                server.GetStorageRanges(storageRange.RootHash!, storageRange.Accounts, storageRange.StartingHash, storageRange.LimitHash, byteLimit, CancellationToken.None);
            batch.StorageRangeResponse = new SlotsAndProofs { PathsAndSlots = slots, Proofs = proofs ?? EmptyByteArrayList.Instance };
            provider.AddStorageRange(storageRange, batch.StorageRangeResponse);
        }
        else if (batch.AccountsToRefreshRequest is { } refresh)
        {
            ValueHash256 path = refresh.Paths[0].PathAndAccount!.Path;
            (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) =
                server.GetAccountRanges(refresh.RootHash, path, path.IncrementPath(), byteLimit, CancellationToken.None);
            batch.AccountsToRefreshResponse = new AccountsAndProofs { PathAndAccounts = accounts, Proofs = proofs };
            provider.RefreshAccounts(refresh, batch.AccountsToRefreshResponse);
        }
        else
        {
            Assert.Fail($"Unexpected request {batch}");
        }
    }

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
        ArrayPoolList<PathWithAccount> accounts = [with(accountCount)];
        for (int i = 0; i < accountCount; i++)
        {
            accounts.Add(new PathWithAccount(TestItem.ValueKeccaks[i], AccountStruct.TotallyEmpty));
        }

        return new StorageRange { Accounts = accounts, StartingHash = Keccak.Zero };
    }

    private static SlotsAndProofs CreateEmptySlotsResponse(int slotListCount)
    {
        ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>> pathsAndSlots = [with(slotListCount)];
        for (int i = 0; i < slotListCount; i++)
        {
            pathsAndSlots.Add(new ArrayPoolList<PathWithStorageSlot>(0));
        }

        return new SlotsAndProofs { PathsAndSlots = pathsAndSlots, Proofs = EmptyByteArrayList.Instance };
    }

    /// <summary>
    /// Builds the storage trie of one account from <paramref name="slotCount"/> slots, and a state trie holding that
    /// account to take storage proofs from.
    /// </summary>
    private static (PathWithAccount Account, PathWithStorageSlot[] Slots, StateTree StateTree) BuildAccountWithStorage(int slotCount)
    {
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
            byte[] rlpValue = Rlp.Encode(value);
            storageTree.Set(slotKey, rlpValue, false);
            slots[i] = new PathWithStorageSlot(slotKey, rlpValue);
        }
        storageTree.Commit();
        Array.Sort(slots, (a, b) => a.Path.CompareTo(b.Path));

        StateTree stateTree = new(store.GetTrieStore(null), LimboLogs.Instance);
        stateTree.Set(accountHash, Build.An.Account.WithBalance(1).WithStorageRoot(storageTree.RootHash).TestObject);
        stateTree.Commit();

        return (new PathWithAccount(accountHash, new Account(0, 1).WithChangedStorageRoot(storageTree.RootHash).ToStruct()), slots, stateTree);
    }

    private static byte[][] ProveStorage(StateTree stateTree, in ValueHash256 accountPath, params ValueHash256[] slotPaths)
    {
        AccountProofCollector proofCollector = new(accountPath.Bytes, slotPaths);
        stateTree.Accept(proofCollector, stateTree.RootHash);
        return proofCollector.BuildResult().StorageProofs.SelectMany(static storageProof => storageProof.Proof).ToArray();
    }

    /// <summary>
    /// Queues <paramref name="account"/>'s storage continuation from <paramref name="origin"/> and answers it the way a
    /// peer with no slots to serve there does: no slot list, only <paramref name="proof"/>.
    /// </summary>
    private static AddRangeResult AnswerStorageContinuationWithProofOnly(
        SnapProvider snapProvider, ProgressTracker progressTracker, PathWithAccount account, ValueHash256 origin, byte[][] proof)
    {
        progressTracker.EnqueueNextSlot(new StorageRange
        {
            Accounts = new ArrayPoolList<PathWithAccount>(1) { account },
            StartingHash = origin,
            LimitHash = Keccak.MaxValue
        });

        progressTracker.IsFinished(out SnapSyncBatch? batch);
        using (batch)
        {
            Assert.That(batch!.StorageRangeRequest, Is.Not.Null);
            batch.StorageRangeResponse = new SlotsAndProofs
            {
                PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(0),
                Proofs = new ByteArrayListAdapter(proof.ToPooledList()),
            };

            AddRangeResult result = snapProvider.AddStorageRange(batch.StorageRangeRequest!, batch.StorageRangeResponse);
            snapProvider.ReleaseRequest(batch, responseHandled: true);
            return result;
        }
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

    [Test]
    public void Local_storage_factory_fault_preserves_retry_without_sample(
        [Values(0, 1, 2, 3)] int faultKind)
    {
        Exception fault = faultKind switch
        {
            0 => new ObjectDisposedException("response buffer"),
            1 => new InvalidOperationException("local proof reader unavailable"),
            2 => new ArgumentException("local reader argument"),
            _ => new IndexOutOfRangeException("local reader index")
        };
        using SnapshotableMemDb db = new();
        NodeStorage nodes = new(db);
        PatriciaSnapTrieFactory realFactory = new(nodes, db, LimboLogs.Instance);
        ISnapTrieFactory factory = Substitute.For<ISnapTrieFactory>();
        int attempts = 0;
        factory.CreateStorageTree(Arg.Any<ValueHash256>()).Returns(call =>
            ++attempts == 1 ? throw fault : realFactory.CreateStorageTree(call.Arg<ValueHash256>()));
        using IContainer container = ProgressTrackerTests.CreateContainer(factory);
        ProgressTracker tracker = container.Resolve<ProgressTracker>();
        SnapProvider provider = container.Resolve<SnapProvider>();
        ProgressTrackerTests.CompleteAccountPartition(tracker, provider);
        using StorageRange source = ProgressTrackerTests.CreateRequest(1);
        tracker.EnqueueAccountStorage(source.Accounts[0]);
        tracker.IsFinished(out SnapSyncBatch? batch);
        Assert.That(batch!.StorageRangeRequest, Is.Not.Null);
        IOwnedReadOnlyList<PathWithStorageSlot> failedSlots = ProgressTrackerTests.ValidSlots();
        batch.StorageRangeResponse = new SlotsAndProofs
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { failedSlots }
        };
        INodeStatsManager stats = Substitute.For<INodeStatsManager>();
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        PeerInfo peer = new(syncPeer);
        Synchronization.SnapSync.SnapSyncFeed feed = new(stats, provider, LimboLogs.Instance);

        Assert.That(Assert.Catch(() => feed.HandleResponse(batch, peer)), Is.SameAs(fault));
        Assert.Throws<ObjectDisposedException>(() => { _ = failedSlots.AsSpan(); });

        stats.DidNotReceive().ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, Arg.Any<long>());
        SnapSyncBatch? retry;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(tracker.IsFinished(out retry), Is.False);
            Assert.That(retry!.StorageRangeRequest!.Accounts[0].Path, Is.EqualTo(source.Accounts[0].Path));
        }
        IOwnedReadOnlyList<PathWithStorageSlot> validSlots = ProgressTrackerTests.ValidSlots();
        retry.StorageRangeResponse = new SlotsAndProofs
        {
            PathsAndSlots = new ArrayPoolList<IOwnedReadOnlyList<PathWithStorageSlot>>(1) { validSlots }
        };
        retry.ResponseMilliseconds = 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(feed.HandleResponse(retry, peer), Is.EqualTo(SyncResponseHandlingResult.OK));
            Assert.That(attempts, Is.EqualTo(2));
            Assert.Throws<ObjectDisposedException>(() => { _ = validSlots.AsSpan(); });
        }
        stats.Received(1).ReportTransferSpeedEvent(syncPeer.Node, TransferSpeedType.SnapRanges, 33);
        StorageTree persisted = new(new RawScopedTrieStore(nodes, source.Accounts[0].Path.ToCommitment()),
            source.Accounts[0].Account.StorageRoot.ToHash256(), LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(persisted.Get(ValueKeccak.Zero.Bytes).ToArray(), Is.EqualTo(new byte[] { 1 }));
            Assert.That(tracker.IsSnapGetRangesFinished(), Is.True);
        }
    }
}
