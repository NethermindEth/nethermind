// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.SnapSync;
using NUnit.Framework;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
using NSubstitute;
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

    private IContainer CreateContainer(
        TestSyncConfig? testSyncConfig = null,
        Func<INodeStorage, ILogManager, ISnapTrieFactory>? factoryCreator = null) =>
        CreateContainerBuilder(testSyncConfig, factoryCreator).Build();

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
            new ByteArrayListAdapter(proof!.StorageProofs![0].Proof!.Concat(proof!.StorageProofs![1].Proof!).ToArray().ToPooledList())), Is.EqualTo(AddRangeResult.OK));
    }

    [Test]
    public void AddStorageRange_ParallelPath_ReturnsLastAccountResult()
    {
        Hash256 firstRoot = TestItem.KeccakA;
        Hash256 secondRoot = TestItem.KeccakB;
        using IContainer container = CreateContainer(
            new TestSyncConfig()
            {
                SnapSyncStorageRangeParallelism = 2
            },
            (_, _) => new RecordingSnapTrieFactory(
                new PathRoot(TestItem.KeccakA, firstRoot),
                new PathRoot(TestItem.KeccakB, secondRoot)));

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        using StorageRange storage = CreateStorageRange(firstRoot, secondRoot);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = CreateStorageResponse(
                CreateSlots(ValueKeccak.MaxValue, ValueKeccak.Zero),
                CreateSlots(ValueKeccak.Zero)),
            Proofs = EmptyByteArrayList.Instance
        };

        Assert.That(snapProvider.AddStorageRange(storage, response), Is.EqualTo(AddRangeResult.OK));
    }

    [Test]
    public void AddStorageRange_ParallelPath_DisposesResponseAfterProcessing()
    {
        Hash256 firstRoot = TestItem.KeccakA;
        Hash256 secondRoot = TestItem.KeccakB;
        using IContainer container = CreateContainer(
            new TestSyncConfig()
            {
                SnapSyncStorageRangeParallelism = 2
            },
            (_, _) => new RecordingSnapTrieFactory(
                new PathRoot(TestItem.KeccakA, firstRoot),
                new PathRoot(TestItem.KeccakB, secondRoot)));

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        using StorageRange storage = CreateStorageRange(firstRoot, secondRoot);
        CountingOwnedReadOnlyList<PathWithStorageSlot> firstSlots = CreateSlots(ValueKeccak.Zero);
        CountingOwnedReadOnlyList<PathWithStorageSlot> secondSlots = CreateSlots(ValueKeccak.Zero);
        CountingByteArrayList proofs = new();
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = CreateStorageResponse(firstSlots, secondSlots),
            Proofs = proofs
        };

        Assert.That(snapProvider.AddStorageRange(storage, response), Is.EqualTo(AddRangeResult.OK));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstSlots.DisposeCount, Is.EqualTo(1));
            Assert.That(secondSlots.DisposeCount, Is.EqualTo(1));
            Assert.That(proofs.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void AddStorageRange_BatchedStorageFactory_CommitsMultiAccountResponseOnce()
    {
        Hash256 firstRoot = TestItem.KeccakA;
        Hash256 secondRoot = TestItem.KeccakB;
        BatchingSnapTrieFactory factory = new(
            new PathRoot(TestItem.KeccakA, firstRoot),
            new PathRoot(TestItem.KeccakB, secondRoot));
        using IContainer container = CreateContainer(
            new TestSyncConfig()
            {
                SnapSyncStorageRangeParallelism = 2
            },
            (_, _) => factory);

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        using StorageRange storage = CreateStorageRange(firstRoot, secondRoot);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = CreateStorageResponse(
                CreateSlots(ValueKeccak.Zero),
                CreateSlots(ValueKeccak.Zero)),
            Proofs = EmptyByteArrayList.Instance
        };

        Assert.That(snapProvider.AddStorageRange(storage, response), Is.EqualTo(AddRangeResult.OK));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.StartedBatches, Is.EqualTo(1));
            Assert.That(factory.CommittedBatches, Is.EqualTo(1));
            Assert.That(factory.AbortedBatches, Is.EqualTo(0));
            Assert.That(factory.StorageTreesWithBatch, Is.EqualTo(2));
        }
    }

    [Test]
    public void AddStorageRange_ParallelBatch_ProcessesStorageAccountsConcurrently()
    {
        Hash256 firstRoot = TestItem.KeccakA;
        Hash256 secondRoot = TestItem.KeccakB;
        using ParallelBatchingSnapTrieFactory factory = new(
            new PathRoot(TestItem.KeccakA, firstRoot),
            new PathRoot(TestItem.KeccakB, secondRoot));
        using IContainer container = CreateContainer(
            new TestSyncConfig()
            {
                SnapSyncStorageRangeParallelism = 2
            },
            (_, _) => factory);

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        using StorageRange storage = CreateStorageRange(firstRoot, secondRoot);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = CreateStorageResponse(
                CreateSlots(ValueKeccak.Zero),
                CreateSlots(ValueKeccak.Zero)),
            Proofs = EmptyByteArrayList.Instance
        };

        Assert.That(snapProvider.AddStorageRange(storage, response), Is.EqualTo(AddRangeResult.OK));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.CommittedBatches, Is.EqualTo(1));
            Assert.That(factory.AbortedBatches, Is.EqualTo(0));
            Assert.That(factory.StorageTreesWithBatch, Is.EqualTo(2));
            Assert.That(factory.MaxConcurrentStorageTrees, Is.EqualTo(2));
        }
    }

    [Test]
    public void AddStorageRange_BatchedStorageFactory_AbortsBatchOnException()
    {
        BatchingDisposedSnapTrieFactory factory = new();
        using IContainer container = CreateContainer(
            new TestSyncConfig()
            {
                SnapSyncStorageRangeParallelism = 2
            },
            (_, _) => factory);

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        using StorageRange storage = CreateStorageRange(TestItem.KeccakA, TestItem.KeccakB);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = CreateStorageResponse(
                CreateSlots(ValueKeccak.Zero),
                CreateSlots(ValueKeccak.Zero)),
            Proofs = EmptyByteArrayList.Instance
        };

        Assert.That(() => snapProvider.AddStorageRange(storage, response), Throws.TypeOf<ObjectDisposedException>());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(factory.StartedBatches, Is.EqualTo(1));
            Assert.That(factory.CommittedBatches, Is.EqualTo(0));
            Assert.That(factory.AbortedBatches, Is.EqualTo(1));
        }
    }

    [Test]
    public void PatriciaStorageBatch_BuffersWritesUntilCommitted()
    {
        MemDb abortedDb = new();
        NodeStorage abortedNodeStorage = new(abortedDb, INodeStorage.KeyScheme.Hash, requirePath: false);
        PatriciaSnapTrieFactory abortedFactory = new(abortedNodeStorage, LimboLogs.Instance);
        using (ISnapStorageBatch abortedBatch = abortedFactory.StartStorageBatch()!)
        {
            using ISnapTree<PathWithStorageSlot> tree = abortedFactory.CreateStorageTree(TestItem.KeccakA, abortedBatch);
            tree.BulkSetAndUpdateRootHash(new[] { new PathWithStorageSlot(ValueKeccak.Zero, [1]) });
            tree.Commit(ValueKeccak.MaxValue);
        }

        MemDb committedDb = new();
        NodeStorage committedNodeStorage = new(committedDb, INodeStorage.KeyScheme.Hash, requirePath: false);
        PatriciaSnapTrieFactory committedFactory = new(committedNodeStorage, LimboLogs.Instance);
        using (ISnapStorageBatch committedBatch = committedFactory.StartStorageBatch()!)
        {
            using ISnapTree<PathWithStorageSlot> tree = committedFactory.CreateStorageTree(TestItem.KeccakA, committedBatch);
            tree.BulkSetAndUpdateRootHash(new[] { new PathWithStorageSlot(ValueKeccak.Zero, [1]) });
            tree.Commit(ValueKeccak.MaxValue);
            committedBatch.Commit();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(abortedDb.Count, Is.EqualTo(0));
            Assert.That(committedDb.Count, Is.GreaterThan(0));
        }
    }

    [Test]
    public void PatriciaStorageBatch_ReplaysBufferedWritesInBoundedBatches()
    {
        const int slotCount = 5_000;
        CountingNodeStorage nodeStorage = new();
        PatriciaSnapTrieFactory factory = new(nodeStorage, LimboLogs.Instance);
        PathWithStorageSlot[] slots = new PathWithStorageSlot[slotCount];
        for (int i = 0; i < slotCount; i++)
        {
            slots[i] = new PathWithStorageSlot(Keccak.Compute(i.ToBigEndianByteArray()), [1]);
        }

        Array.Sort(slots, static (left, right) => left.Path.CompareTo(right.Path));

        using (ISnapStorageBatch batch = factory.StartStorageBatch()!)
        {
            using ISnapTree<PathWithStorageSlot> tree = factory.CreateStorageTree(TestItem.KeccakA, batch);
            tree.BulkSetAndUpdateRootHash(slots);
            tree.Commit(ValueKeccak.MaxValue);
            batch.Commit();
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodeStorage.DisposedBatchSizes, Has.Count.GreaterThan(1));
            Assert.That(nodeStorage.DisposedBatchSizes, Has.All.LessThanOrEqualTo(2 * 1024));
        }
    }

    [Test]
    public void AddStorageRange_ParallelPath_UnwrapsObjectDisposedException()
    {
        using IContainer container = CreateContainer(
            new TestSyncConfig()
            {
                SnapSyncStorageRangeParallelism = 2
            },
            (_, _) => new DisposedSnapTrieFactory());

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        using StorageRange storage = CreateStorageRange(TestItem.KeccakA, TestItem.KeccakB);
        using SlotsAndProofs response = new()
        {
            PathsAndSlots = CreateStorageResponse(
                CreateSlots(ValueKeccak.Zero),
                CreateSlots(ValueKeccak.Zero)),
            Proofs = EmptyByteArrayList.Instance
        };

        Assert.That(() => snapProvider.AddStorageRange(storage, response), Throws.TypeOf<ObjectDisposedException>());
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

        (SnapServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

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

        (SnapServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);

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

    [Test]
    public void AddAccountRange_UsesSnapshotableCodeDb_ForExistingCodeHashes()
    {
        byte[] code = [1, 2, 3];
        Hash256 codeHash = Keccak.Compute(code);
        (Hash256, Account)[] entries =
        [
            (TestItem.KeccakA, new Account(0, 1).WithChangedCodeHash(codeHash)),
        ];

        (SnapServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);
        SnapshotableMemDb codeDb = new();
        codeDb[codeHash.Bytes] = code;

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .WithSuggestedHeaderOfStateRoot(root)
            .AddKeyedSingleton<IDb>(DbNames.Code, _ => codeDb)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.False);
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = ss.GetAccountRanges(
            root,
            batch!.AccountRangeRequest!.StartingHash,
            batch.AccountRangeRequest.LimitHash,
            1.MB,
            default);

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        Assert.That(snapProvider.AddAccountRange(batch.AccountRangeRequest, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));
        Assert.That(progressTracker.IsFinished(out batch), Is.True);
        Assert.That(batch, Is.Null);
    }

    [Test]
    public void AddAccountRange_DoesNotSnapshotCodeDb_WhenAccountsHaveNoCode()
    {
        (Hash256, Account)[] entries =
        [
            (TestItem.KeccakA, new Account(0, 1)),
        ];

        (SnapServer ss, Hash256 root) = BuildSnapServerFromEntries(entries);
        IDb codeDb = Substitute.For<IDb, IKeyValueStoreWithSnapshot>();

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .WithSuggestedHeaderOfStateRoot(root)
            .AddKeyedSingleton<IDb>(DbNames.Code, _ => codeDb)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ProgressTracker progressTracker = container.Resolve<ProgressTracker>();

        Assert.That(progressTracker.IsFinished(out SnapSyncBatch? batch), Is.False);
        (IOwnedReadOnlyList<PathWithAccount> accounts, IByteArrayList proofs) = ss.GetAccountRanges(
            root,
            batch!.AccountRangeRequest!.StartingHash,
            batch.AccountRangeRequest.LimitHash,
            1.MB,
            default);

        using AccountsAndProofs accountsAndProofs = new();
        accountsAndProofs.PathAndAccounts = accounts;
        accountsAndProofs.Proofs = proofs;

        Assert.That(snapProvider.AddAccountRange(batch.AccountRangeRequest, accountsAndProofs), Is.EqualTo(AddRangeResult.OK));
        ((IKeyValueStoreWithSnapshot)codeDb).DidNotReceive().CreateSnapshot();
    }

    [Test]
    public void AddCodes_WritesRequestedCodeThroughBatchSpanPath()
    {
        byte[] code = [1, 2, 3];
        byte[] unexpectedCode = [4, 5, 6];
        ValueHash256 codeHash = Keccak.Compute(code);
        RecordingCodeDb codeDb = new();

        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .AddKeyedSingleton<IDb>(DbNames.Code, _ => codeDb)
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        ByteArrayListAdapter codes = new(new List<byte[]> { code, unexpectedCode }.ToPooledList());

        snapProvider.AddCodes([codeHash], codes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(codeDb.LastBatch, Is.Not.Null);
            Assert.That(codeDb.LastBatch!.PutSpanWrites, Has.Count.EqualTo(1));
            Assert.That(codeDb.LastBatch.SetWriteCount, Is.Zero);
            Assert.That(codeDb.LastBatch.PutSpanWrites[0].Key, Is.EqualTo(codeHash.Bytes.ToArray()));
            Assert.That(codeDb.LastBatch.PutSpanWrites[0].Value, Is.EqualTo(code));
            Assert.That(codeDb.LastBatch.PutSpanWrites[0].Flags, Is.EqualTo(WriteFlags.None));
            Assert.That(codeDb.Get(codeHash.Bytes), Is.EqualTo(code));
        }
    }

    [Test]
    public void AddCodes_DisposesCodes_WhenWriteBatchCreationFails()
    {
        using IContainer container = CreateContainerBuilder(new TestSyncConfig()
        {
            SnapSyncAccountRangePartitionCount = 1
        })
            .AddKeyedSingleton<IDb>(DbNames.Code, _ => new ThrowingCodeDb())
            .Build();

        SnapProvider snapProvider = container.Resolve<SnapProvider>();
        DisposableByteArrayList codes = new([1, 2, 3]);

        Assert.That(() => snapProvider.AddCodes([], codes), Throws.InvalidOperationException);
        Assert.That(codes.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public void Persisted_check_scope_uses_snapshot_and_disposes_once_when_owner_disposes_first()
    {
        CountingReadSnapshot snapshot = new() { Exists = false };
        MutableNodeStorage storage = new(snapshot) { Exists = true };
        NodeStoragePersistedCheckScope persistedCheckScope = new(storage);

        IDisposable scope = persistedCheckScope.Begin();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedCheckScope.Current.KeyExists(null, TreePath.Empty, TestItem.KeccakA), Is.False);
            Assert.That(snapshot.DisposeCount, Is.Zero);
        }

        persistedCheckScope.Dispose();
        scope.Dispose();
        scope.Dispose();
        persistedCheckScope.Dispose();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(persistedCheckScope.Current.KeyExists(null, TreePath.Empty, TestItem.KeccakA), Is.True);
            Assert.That(snapshot.DisposeCount, Is.EqualTo(1));
        }
    }

    [Test]
    public void Persisted_check_scope_falls_back_to_live_storage_without_snapshot_support()
    {
        MutableNodeStorage storage = new(null) { Exists = false };
        NodeStoragePersistedCheckScope persistedCheckScope = new(storage);

        using IDisposable scope = persistedCheckScope.Begin();
        storage.Exists = true;

        Assert.That(persistedCheckScope.Current.KeyExists(null, TreePath.Empty, TestItem.KeccakA), Is.True);
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
            Rlp.ValueDecoderContext context = Bytes.FromHexString(asReq.Accounts[i]).AsRlpValueContext();
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

    private static (SnapServer, Hash256) BuildSnapServerFromEntries((Hash256, Account)[] entries)
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

        SnapServer ss = new(trieStore.AsReadOnly(), new TestMemDb(), LimboLogs.Instance);
        return (ss, st.RootHash);
    }

    private static StorageRange CreateStorageRange(Hash256 firstStorageRoot, Hash256 secondStorageRoot) =>
        new()
        {
            StartingHash = ValueKeccak.Zero,
            LimitHash = ValueKeccak.MaxValue,
            Accounts = new ArrayPoolList<PathWithAccount>(2)
            {
                new(TestItem.KeccakA, new Account(0, 1).WithChangedStorageRoot(firstStorageRoot)),
                new(TestItem.KeccakB, new Account(0, 1).WithChangedStorageRoot(secondStorageRoot))
            }
        };

    private static CountingOwnedReadOnlyList<PathWithStorageSlot> CreateSlots(params ValueHash256[] paths)
    {
        PathWithStorageSlot[] slots = new PathWithStorageSlot[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            slots[i] = new PathWithStorageSlot(paths[i], []);
        }

        return new CountingOwnedReadOnlyList<PathWithStorageSlot>(slots);
    }

    private static CountingOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>> CreateStorageResponse(params IOwnedReadOnlyList<PathWithStorageSlot>[] slots) =>
        new(slots);

    private readonly record struct PathRoot(ValueHash256 Path, Hash256 RootHash);

    private sealed class RecordingSnapTrieFactory(params PathRoot[] storageRoots) : ISnapTrieFactory
    {
        public ISnapTree<PathWithAccount> CreateStateTree() =>
            throw new NotSupportedException();

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath)
        {
            for (int i = 0; i < storageRoots.Length; i++)
            {
                if (storageRoots[i].Path == accountPath)
                {
                    return new FixedRootSnapStorageTree(storageRoots[i].RootHash);
                }
            }

            throw new InvalidOperationException($"Unexpected storage account path {accountPath}");
        }
    }

    private sealed class BatchingSnapTrieFactory(params PathRoot[] storageRoots) : ISnapTrieFactory
    {
        public int StartedBatches { get; private set; }
        public int CommittedBatches { get; private set; }
        public int AbortedBatches { get; private set; }
        public int StorageTreesWithBatch { get; private set; }

        public ISnapTree<PathWithAccount> CreateStateTree() =>
            throw new NotSupportedException();

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) =>
            CreateStorageTree(accountPath, storageBatch: null);

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath, ISnapStorageBatch? storageBatch)
        {
            if (storageBatch is not null)
            {
                StorageTreesWithBatch++;
            }

            for (int i = 0; i < storageRoots.Length; i++)
            {
                if (storageRoots[i].Path == accountPath)
                {
                    return new FixedRootSnapStorageTree(storageRoots[i].RootHash);
                }
            }

            throw new InvalidOperationException($"Unexpected storage account path {accountPath}");
        }

        public ISnapStorageBatch StartStorageBatch()
        {
            StartedBatches++;
            return new RecordingStorageBatch(this);
        }

        private sealed class RecordingStorageBatch(BatchingSnapTrieFactory factory) : ISnapStorageBatch
        {
            private bool _committed;
            private bool _disposed;

            public void Commit()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _committed = true;
                factory.CommittedBatches++;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_committed)
                {
                    factory.AbortedBatches++;
                }
            }
        }
    }

    private sealed class ParallelBatchingSnapTrieFactory(params PathRoot[] storageRoots) : ISnapTrieFactory, IDisposable
    {
        private readonly ManualResetEventSlim _secondStorageTreeEntered = new();
        private int _activeStorageTrees;
        private int _enteredStorageTrees;
        private int _maxConcurrentStorageTrees;
        private int _storageTreesWithBatch;

        public int CommittedBatches { get; private set; }
        public int AbortedBatches { get; private set; }
        public int StorageTreesWithBatch => Volatile.Read(ref _storageTreesWithBatch);
        public int MaxConcurrentStorageTrees => Volatile.Read(ref _maxConcurrentStorageTrees);

        public ISnapTree<PathWithAccount> CreateStateTree() =>
            throw new NotSupportedException();

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) =>
            CreateStorageTree(accountPath, storageBatch: null);

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath, ISnapStorageBatch? storageBatch)
        {
            if (storageBatch is not null)
            {
                Interlocked.Increment(ref _storageTreesWithBatch);
            }

            for (int i = 0; i < storageRoots.Length; i++)
            {
                if (storageRoots[i].Path == accountPath)
                {
                    return new ConcurrentFixedRootSnapStorageTree(storageRoots[i].RootHash, this);
                }
            }

            throw new InvalidOperationException($"Unexpected storage account path {accountPath}");
        }

        public ISnapStorageBatch StartStorageBatch() =>
            new RecordingParallelStorageBatch(this);

        public void EnterStorageTree()
        {
            int active = Interlocked.Increment(ref _activeStorageTrees);
            UpdateMaxConcurrentStorageTrees(active);

            int entered = Interlocked.Increment(ref _enteredStorageTrees);
            if (entered == 1)
            {
                if (!_secondStorageTreeEntered.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("The second storage tree was not processed concurrently.");
                }
            }
            else if (entered == 2)
            {
                _secondStorageTreeEntered.Set();
            }
        }

        public void ExitStorageTree() =>
            Interlocked.Decrement(ref _activeStorageTrees);

        public void Dispose() =>
            _secondStorageTreeEntered.Dispose();

        private void UpdateMaxConcurrentStorageTrees(int value)
        {
            int current;
            do
            {
                current = Volatile.Read(ref _maxConcurrentStorageTrees);
                if (value <= current)
                {
                    return;
                }
            } while (Interlocked.CompareExchange(ref _maxConcurrentStorageTrees, value, current) != current);
        }

        private sealed class RecordingParallelStorageBatch(ParallelBatchingSnapTrieFactory factory) : IParallelSnapStorageBatch
        {
            private bool _committed;
            private bool _disposed;

            public void Commit()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _committed = true;
                factory.CommittedBatches++;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_committed)
                {
                    factory.AbortedBatches++;
                }
            }
        }
    }

    private sealed class BatchingDisposedSnapTrieFactory : ISnapTrieFactory
    {
        public int StartedBatches { get; private set; }
        public int CommittedBatches { get; private set; }
        public int AbortedBatches { get; private set; }

        public ISnapTree<PathWithAccount> CreateStateTree() =>
            throw new NotSupportedException();

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) =>
            new DisposedSnapStorageTree();

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath, ISnapStorageBatch? storageBatch) =>
            new DisposedSnapStorageTree();

        public ISnapStorageBatch StartStorageBatch()
        {
            StartedBatches++;
            return new RecordingStorageBatch(this);
        }

        private sealed class RecordingStorageBatch(BatchingDisposedSnapTrieFactory factory) : ISnapStorageBatch
        {
            private bool _committed;
            private bool _disposed;

            public void Commit()
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _committed = true;
                factory.CommittedBatches++;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                if (!_committed)
                {
                    factory.AbortedBatches++;
                }
            }
        }
    }

    private sealed class FixedRootSnapStorageTree(Hash256 rootHash) : ISnapTree<PathWithStorageSlot>
    {
        public Hash256 RootHash { get; private set; } = Keccak.Zero;

        public void SetRootFromProof(TrieNode root)
        {
        }

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
            false;

        public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithStorageSlot> entries) =>
            RootHash = rootHash;

        public void Commit(ValueHash256 upperBound)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class ConcurrentFixedRootSnapStorageTree(Hash256 rootHash, ParallelBatchingSnapTrieFactory factory) : ISnapTree<PathWithStorageSlot>
    {
        public Hash256 RootHash { get; private set; } = Keccak.Zero;

        public void SetRootFromProof(TrieNode root)
        {
        }

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
            false;

        public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithStorageSlot> entries)
        {
            factory.EnterStorageTree();
            try
            {
                RootHash = rootHash;
            }
            finally
            {
                factory.ExitStorageTree();
            }
        }

        public void Commit(ValueHash256 upperBound)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class DisposedSnapTrieFactory : ISnapTrieFactory
    {
        public ISnapTree<PathWithAccount> CreateStateTree() =>
            throw new NotSupportedException();

        public ISnapTree<PathWithStorageSlot> CreateStorageTree(in ValueHash256 accountPath) =>
            new DisposedSnapStorageTree();
    }

    private sealed class DisposedSnapStorageTree : ISnapTree<PathWithStorageSlot>
    {
        public Hash256 RootHash => Keccak.Zero;

        public void SetRootFromProof(TrieNode root)
        {
        }

        public bool IsPersisted(in TreePath path, in ValueHash256 keccak) =>
            false;

        public void BulkSetAndUpdateRootHash(IReadOnlyList<PathWithStorageSlot> entries) =>
            throw new ObjectDisposedException(nameof(DisposedSnapStorageTree));

        public void Commit(ValueHash256 upperBound)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingOwnedReadOnlyList<T>(params T[] items) : IOwnedReadOnlyList<T>
    {
        public int Count => items.Length;
        public int DisposeCount { get; private set; }

        public T this[int index] => items[index];

        public ReadOnlySpan<T> AsSpan() =>
            items;

        public IEnumerator<T> GetEnumerator()
        {
            for (int i = 0; i < items.Length; i++)
            {
                yield return items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public void Dispose() =>
            DisposeCount++;
    }

    private sealed class MutableNodeStorage(CountingReadSnapshot? snapshot) : INodeStorage, INodeStorageWithReadSnapshot
    {
        public INodeStorage.KeyScheme Scheme { get; set; }
        public bool RequirePath => false;
        public bool Exists { get; set; }

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
            Exists ? [] : null;

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) =>
            Exists;

        public INodeStorageReadSnapshot? CreateReadSnapshot() =>
            snapshot;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
        {
        }

        public INodeStorage.IWriteBatch StartWriteBatch() =>
            throw new NotSupportedException();

        public void Flush(bool onlyWal)
        {
        }

        public void Compact()
        {
        }
    }

    private sealed class CountingReadSnapshot : INodeStorageReadSnapshot
    {
        public INodeStorage.KeyScheme Scheme { get; } = INodeStorage.KeyScheme.Hash;
        public bool RequirePath => false;
        public bool Exists { get; init; }
        public int DisposeCount { get; private set; }

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
            Exists ? [] : null;

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) =>
            Exists;

        public void Dispose() =>
            DisposeCount++;
    }

    private sealed class CountingNodeStorage : INodeStorage
    {
        public List<int> DisposedBatchSizes { get; } = [];

        public INodeStorage.KeyScheme Scheme { get; set; } = INodeStorage.KeyScheme.Hash;

        public bool RequirePath => false;

        public byte[]? Get(Hash256? address, in TreePath path, in ValueHash256 keccak, ReadFlags readFlags = ReadFlags.None) =>
            null;

        public bool KeyExists(in ValueHash256? address, in TreePath path, in ValueHash256 hash) =>
            false;

        public void Set(Hash256? address, in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> data, WriteFlags writeFlags = WriteFlags.None)
        {
        }

        public INodeStorage.IWriteBatch StartWriteBatch() =>
            new CountingWriteBatch(this);

        public void Flush(bool onlyWal)
        {
        }

        public void Compact()
        {
        }

        private sealed class CountingWriteBatch(CountingNodeStorage nodeStorage) : INodeStorage.IWriteBatch
        {
            private int _count;
            private bool _disposed;

            public void Set(Hash256? address, in TreePath path, in ValueHash256 currentNodeKeccak, ReadOnlySpan<byte> data, WriteFlags writeFlags)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _count++;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                nodeStorage.DisposedBatchSizes.Add(_count);
            }
        }
    }

    private sealed class RecordingCodeDb : TestMemDb
    {
        public RecordingWriteBatch? LastBatch { get; private set; }

        public override IWriteBatch StartWriteBatch()
        {
            LastBatch = new RecordingWriteBatch(this);
            return LastBatch;
        }
    }

    private sealed class ThrowingCodeDb : TestMemDb
    {
        public override IWriteBatch StartWriteBatch() =>
            throw new InvalidOperationException();
    }

    private sealed class DisposableByteArrayList(byte[] item) : IByteArrayList
    {
        public int Count => 1;
        public int DisposeCount { get; private set; }
        public ReadOnlySpan<byte> this[int index] => index == 0 ? item : throw new IndexOutOfRangeException();

        public void Dispose() =>
            DisposeCount++;
    }

    private sealed class CountingByteArrayList : IByteArrayList
    {
        public int Count => 0;
        public int DisposeCount { get; private set; }
        public ReadOnlySpan<byte> this[int index] => throw new IndexOutOfRangeException();

        public void Dispose() =>
            DisposeCount++;
    }

    private sealed class RecordingWriteBatch(RecordingCodeDb db) : IWriteBatch
    {
        public List<(byte[] Key, byte[] Value, WriteFlags Flags)> PutSpanWrites { get; } = [];

        public int SetWriteCount { get; private set; }

        public void Clear() =>
            PutSpanWrites.Clear();

        public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
        {
            SetWriteCount++;
            db.Set(key, value, flags);
        }

        public void PutSpan(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None)
        {
            byte[] valueCopy = value.ToArray();
            PutSpanWrites.Add((key.ToArray(), valueCopy, flags));
            db.Set(key, valueCopy, flags);
        }

        public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
