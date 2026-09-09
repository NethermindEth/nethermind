// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;
using NSubstitute;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

public class ImportPbtFromPreimageFlatTests
{
    private const ulong SourceBlock = 7;

    /// <summary>Header root that import must use as the resulting state's key.</summary>
    /// <remarks>It is unrelated to fixture tree roots to prevent accidental matches.</remarks>
    private static readonly Hash256 SourceStateRoot = TestItem.KeccakA;

    // Zero uses the built-in window; small values force multiple windows with the same root.
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public async Task Imports_preimage_flat_state_into_pbt_and_exits(int windowSize)
    {
        PbtConfig config = new() { ImportWindowSize = windowSize };

        // More than 128 chunks exercises the overflow-code zone end-to-end.
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);      // header-region slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);     // storage-zone slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        // A second contract with the same code exercises content-addressed overflow-chunk deduplication.
        PbtReferenceModel.SetAccount(model, TestItem.AddressC, 9, 5, bigCode);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            // A non-empty flat storage root triggers storage fan-out; PBT omits it.
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 70, SlotValue.FromSpanWithoutLeadingZero([0x07]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
            batch.SetAccount(TestItem.AddressC, new Account(9, 5).WithChangedCodeHash(bigCodeHash));
        }

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        // Both phases need the same column database; otherwise phase two scans nothing.
        ImportPbtFromPreimageFlat step = new(flatSource, codeDb, pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)), "the state is keyed by the source's header root");
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "with the folded tree's own root recorded beside it");
        Assert.That(reader.GetCodeReference(bigCodeHash.ValueHash256), Is.EqualTo(2), "shared code references survive later account changes");
        PbtScanReport scan = await new PbtScanner(pbtDb, config, LimboLogs.Instance).Scan(CancellationToken.None, null, 4, TimeProvider.System);
        Assert.That(scan.IsValid, Is.True, scan.Format());
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressC)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(reader.GetCode(bigCodeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(bigCode));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!.StorageRoot, Is.EqualTo(TestItem.KeccakA));
        Assert.That(pbtDb.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));
    }

    /// <summary>Verifies merge-joining header-only storage and accounts without storage or code.</summary>
    [Test]
    public async Task Imports_accounts_with_only_header_storage_and_with_none()
    {
        PbtConfig config = new();

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 2, 200);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 0, 0x11);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 63, 0x22);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(2, 200).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 0, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(TestItem.AddressB, 63, SlotValue.FromSpanWithoutLeadingZero([0x22]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    /// <summary>
    /// Flat storage keys interleave accounts sharing their leading four address bytes, so copied slots
    /// must remain associated with their originating account.
    /// </summary>
    [Test]
    public async Task Imports_slots_of_accounts_sharing_a_storage_key_prefix()
    {
        PbtConfig config = new();

        // Equal leading address bytes cause their flat-storage slots to interleave.
        Address first = new(Bytes.FromHexString("0x00000000000000000000000000000000000000aa"));
        Address second = new(Bytes.FromHexString("0x00000000000000000000000000000000000000bb"));

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, first, 1, 100);
        PbtReferenceModel.SetAccount(model, second, 2, 200);
        PbtReferenceModel.SetSlot(model, first, 1, 0x11);      // header-region slot
        PbtReferenceModel.SetSlot(model, first, 1000, 0x22);   // storage-zone slot
        PbtReferenceModel.SetSlot(model, second, 1, 0x33);
        PbtReferenceModel.SetSlot(model, second, 1000, 0x44);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(first, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetAccount(second, new Account(2, 200).WithChangedStorageRoot(TestItem.KeccakB));
            batch.SetStorage(first, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(first, 1000, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            batch.SetStorage(second, 1, SlotValue.FromSpanWithoutLeadingZero([0x33]));
            batch.SetStorage(second, 1000, SlotValue.FromSpanWithoutLeadingZero([0x44]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, first, 1000)).ToArray(), Is.EqualTo(((UInt256)0x22).ToBigEndian()));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, second, 1000)).ToArray(), Is.EqualTo(((UInt256)0x44).ToBigEndian()));
    }

    /// <summary>
    /// A retry after a pre-publication crash must clear staged new-format rows without reading stale nodes.
    /// </summary>
    /// <param name="clearKeyChunk">A value of 1 reopens the view after each deleted key, verifying the exclusive resume cursor.</param>
    [TestCase(10_000)]
    [TestCase(1)]
    public async Task Import_mode_recovers_an_interrupted_epoch_11_attempt(int clearKeyChunk)
    {
        PbtConfig config = new() { ImportFromPreimageFlat = true };

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());

        async Task<ValueHash256> Import()
        {
            RecordingExitSource exitSource = new();
            ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, config, exitSource, LimboLogs.Instance)
            {
                ClearKeyChunk = clearKeyChunk,
            };
            await step.Execute(CancellationToken.None);
            Assert.That(exitSource.ExitCode, Is.EqualTo(0));

            using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
            return reader.CurrentRoot;
        }

        using (IPbtPersistence.IWriteBatch staging = pbtTarget.CreateStagingWriteBatch(WriteFlags.None))
        {
            staging.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressC), new Account(1, 2));
            PbtFullKey staleNodeKey = new([0x80]);
            PbtNodePath groupKey = new([], 0);
            using PbtNodeGroupStore staleNodes = new();
            staleNodes.SetNode(groupKey, PbtNodeCodec.EncodeLeaf(staleNodeKey, TestItem.KeccakA.Bytes));
            using RefCountingMemory? payload = staleNodes.GetNodeGroup(groupKey);
            staging.SetNodeGroup(groupKey, payload);
            staging.Commit();
        }
        byte[] maximumLengthKey = new byte[PbtStorageFullKey.MaxLength];
        maximumLengthKey.AsSpan().Fill(0xFF);
        pbtDb.GetColumnDb(PbtColumns.Storages)[maximumLengthKey] = TestItem.KeccakA.Bytes.ToArray();

        IDb metadata = pbtDb.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Get("currentState"u8), Is.Null);
            Assert.That(metadata.Get("validState"u8), Is.Null);
            Assert.That(() => new PbtRocksDbPersistence(pbtDb, new PbtConfig()),
                Throws.TypeOf<InvalidDataException>().With.Message.Contains("interrupted initialization"));
        }

        ValueHash256 rebuilt = await Import();
        Assert.That(rebuilt, Is.EqualTo(PbtReferenceModel.Root(model)), "a restart over an interrupted import must reproduce the source root");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Get("validState"u8), Is.EqualTo(new byte[] { 1 }));
            Assert.That(pbtDb.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty, "import must not populate a split-leaf column");
            Assert.That(pbtDb.GetColumnDb(PbtColumns.Storages).Get(maximumLengthKey), Is.Null, "the full keyspace must be cleared during retry");
            Assert.That(() => new PbtRocksDbPersistence(pbtDb, new PbtConfig()), Throws.Nothing);
        }
    }

    [Test]
    public void Scanner_external_sort_and_fold_are_bounded([Values(0, 1, 33, 257)] int count)
    {
        using PbtScanLeafSorter sorter = new(bufferCapacity: 4, mergeFanIn: 2);
        using PbtNodeGroupStore expectedStore = new();
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        for (int index = count - 1; index >= 0; index--)
        {
            byte[] bytes = new byte[index % 2 == 0 ? 34 : 66];
            bytes[0] = index % 2 == 0 ? (byte)0 : (byte)2;
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(bytes.Length - 4), index);
            PbtStorageFullKey key = new(bytes);
            sorter.Add(key, TestItem.KeccakA.ValueHash256, CancellationToken.None);
            sorter.Add(key, TestItem.KeccakA.ValueHash256, CancellationToken.None);
            changes.Set(key, TestItem.KeccakA.ValueHash256);
        }
        ValueHash256 expectedRoot = TrieUpdater.UpdateRoot(expectedStore, default, changes.Build());
        long matched = 0;
        PbtScanTreeBuilder builder = new((path, encoding) =>
        {
            PbtNodeGroupLocation location = PbtFourLevelGroupGeometry.Locate(path);
            using RefCountingMemory? payload = expectedStore.GetNodeGroup(location.GroupKey);
            Assert.That(payload, Is.Not.Null);
            PbtNodeGroupReader reader = new(location.GroupKey, payload!.GetSpan());
            Assert.That(reader.GetNode(location.Position).SequenceEqual(encoding), Is.True, $"node at {path}");
            matched++;
        });
        int unique = 0;
        foreach ((PbtStorageFullKey key, ValueHash256 value) in sorter.GetSorted(CancellationToken.None))
        {
            builder.Add(key, value);
            unique++;
        }
        ValueHash256 actualRoot = builder.Finish();
        string temporaryDirectory = sorter.TemporaryDirectory;
        sorter.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actualRoot, Is.EqualTo(expectedRoot));
            Assert.That(unique, Is.EqualTo(count));
            Assert.That(matched, Is.EqualTo(count == 0 ? 0 : count * 2 - 1));
            Assert.That(sorter.PeakBufferedRecords, Is.LessThanOrEqualTo(4));
            Assert.That(sorter.PeakMergeReaders, Is.LessThanOrEqualTo(2));
            Assert.That(builder.PeakFrontier, Is.LessThanOrEqualTo(PbtStorageFullKey.MaxLength * 8));
            Assert.That(Directory.Exists(temporaryDirectory), Is.False);
            if (count > 32) Assert.That(sorter.RunCount, Is.GreaterThan(count / 2 + 2), "multiple merge passes");
        }
    }

    [Test]
    public void Scanner_sort_files_are_cleaned_after_failure([Values("cancel", "io", "conflict")] string failure)
    {
        string temporaryDirectory;
        using CancellationTokenSource cancellation = new();
        using (PbtScanLeafSorter sorter = new(bufferCapacity: 1, mergeFanIn: 2, progress: (phase, count) =>
        {
            if (failure == "cancel" && phase.StartsWith("merge") && count == 1) cancellation.Cancel();
        }))
        {
            temporaryDirectory = sorter.TemporaryDirectory;
            PbtStorageFullKey key = new(Bytes.FromHexString("0x0001"));
            sorter.Add(key, TestItem.KeccakA.ValueHash256, CancellationToken.None);
            sorter.Add(failure == "conflict" ? key : new PbtStorageFullKey(Bytes.FromHexString("0x0002")), TestItem.KeccakB.ValueHash256, CancellationToken.None);
            if (failure == "io") File.Delete(Path.Combine(temporaryDirectory, "0-1.bin"));
            Assert.That(() =>
            {
                foreach (KeyValuePair<PbtStorageFullKey, ValueHash256> _ in sorter.GetSorted(cancellation.Token)) { }
            }, failure == "cancel" ? Throws.InstanceOf<OperationCanceledException>() : failure == "conflict" ? Throws.TypeOf<InvalidDataException>() : Throws.InstanceOf<IOException>());
        }
        Assert.That(Directory.Exists(temporaryDirectory), Is.False);
    }

    [Test]
    public async Task Scanner_logs_progress_and_cleans_scratch_files()
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        _ = new PbtRocksDbPersistence(db, new PbtConfig());
        for (int index = 0; index < 20; index++)
        {
            byte[] key = new byte[32];
            key[^1] = (byte)index;
            db.GetColumnDb(PbtColumns.Accounts)[key] = Nethermind.Serialization.Rlp.Rlp.Encode(new Account(1, 100)).Bytes;
        }
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        ILogManager logs = NSubstitute.Substitute.For<ILogManager>();
        ILogger scanLogger = new(logger);
        logs.GetClassLogger<PbtScanner>().Returns(scanLogger);
        DirectoryInfo temporaryParent = Directory.CreateTempSubdirectory("pbt-scan-test-");
        try
        {
            await new PbtScanner(db, new PbtConfig(), logs).Scan(CancellationToken.None, temporaryParent.FullName, 2, new ScanTimeProvider());
            logger.Received().Info(NSubstitute.Arg.Is<string>(message => message.Contains("scanning Accounts: 1 items")));
            logger.Received().Info(NSubstitute.Arg.Is<string>(message => message.Contains("merge pass")));
            logger.Received().Info(NSubstitute.Arg.Is<string>(message => message.Contains("folding tree")));
            Assert.That(Directory.EnumerateFileSystemEntries(temporaryParent.FullName), Is.Empty);
        }
        finally { temporaryParent.Delete(true); }
    }

    private sealed class ScanTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => 1;
        public override long GetTimestamp() => _timestamp += 11;
    }

    [Test]
    public async Task Scanner_detects_missing_and_unreachable_nodes_and_ignores_orphan_code([Values("valid", "missing", "extra")] string scenario)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtConfig config = new();
        PbtRocksDbPersistence persistence = new(db, config);
        PbtStorageFullKey storageKey = PbtStateKey.Storage(TestItem.AddressA, 1000);
        using PbtWriteBatchBuilder<PbtStorageFullKey> changes = new(0);
        changes.Set(storageKey, TestItem.KeccakA.ValueHash256);
        using PbtNodeGroupStore nodes = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(nodes, default, changes.Build());
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), root, WriteFlags.None))
        {
            batch.SetSlot(storageKey, EvmWordSlot.FromStripped(TestItem.KeccakA.Bytes));
            foreach (IPbtNodePath path in nodes.EnumerateNodeGroupKeys())
            {
                using RefCountingMemory? payload = nodes.GetNodeGroup(path);
                batch.SetNodeGroup(path, payload);
            }
            batch.Commit();
        }
        byte[] orphanCode = Bytes.FromHexString("0x6001600055");
        db.GetColumnDb(PbtColumns.Codes)[Keccak.Compute(orphanCode).Bytes] = orphanCode;
        if (scenario == "missing") db.GetColumnDb(PbtColumns.NodeGroups).Remove(new PbtNodePath([], 0).Encode());
        if (scenario == "extra")
        {
            IPbtNodePath path = new PbtNodePath(Bytes.FromHexString("0xf0"), 4);
            PbtStorageFullKey extraKey = new(Bytes.FromHexString("0xf0"));
            PbtNodeRecord record = new(path, PbtNodeCodec.EncodeLeaf(extraKey, TestItem.KeccakB.Bytes));
            // Preserve the expected root and add a structurally valid but unreachable node.
            using RefCountingMemory? payload = nodes.GetNodeGroup(new PbtNodePath([], 0));
            PbtNodeGroupReader reader = new(new PbtNodePath([], 0), payload!.GetSpan());
            PbtNodeRecord rootRecord = new(new PbtNodePath([], 0), reader.GetNode(PbtFourLevelGroupGeometry.RootPosition));
            BufferWriter writer = new(new byte[1024]);
            PbtNodeGroupCodec.Encode(ref writer, new PbtNodePath([], 0), new[] { record, rootRecord });
            db.GetColumnDb(PbtColumns.NodeGroups)[new PbtNodePath([], 0).Encode()] = writer.WrittenSpan.ToArray();
        }
        PbtScanReport report = await new PbtScanner(db, config, LimboLogs.Instance).Scan(CancellationToken.None, null, 1, TimeProvider.System);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.RootMatches, Is.True);
            Assert.That(report.LeafCount, Is.EqualTo(1));
            Assert.That(report.InvalidLeafCount, Is.Zero);
            Assert.That(report.IsValid, Is.EqualTo(scenario == "valid"));
            Assert.That(report.InvalidNodeCount, scenario == "valid" ? Is.Zero : Is.GreaterThan(0));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Scanner_reports_node_corruption_and_root_mismatch(bool corruptNode)
    {
        PbtConfig config = new();
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, config);
        ValueHash256 addressHash = PbtKeyDerivation.AddressKeyHash(TestItem.AddressA);
        Account account = new(1, 100);
        using PbtWriteBatchBuilder<PbtFullKey> changes = new(0);
        foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(addressHash, account, null)) changes.Set(key, value);
        using PbtNodeGroupStore nodeStore = new();
        ValueHash256 root = TrieUpdater.UpdateRoot(nodeStore, default, changes.Build());
        ValueHash256 persistedRoot = corruptNode ? root : TestItem.KeccakB.ValueHash256;
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis,
            new StateId(SourceBlock, SourceStateRoot),
            persistedRoot,
            WriteFlags.None))
        {
            batch.SetAccount(addressHash, account);
            foreach (IPbtNodePath groupKey in nodeStore.EnumerateNodeGroupKeys())
            {
                using RefCountingMemory? payload = nodeStore.GetNodeGroup(groupKey);
                batch.SetNodeGroup(groupKey, payload);
            }
            batch.Commit();
        }
        if (corruptNode)
            db.GetColumnDb(PbtColumns.NodeGroups)[new PbtNodePath([], 0).Encode()] = [0x7F];

        PbtScanReport report = await new PbtScanner(db, config, LimboLogs.Instance).Scan(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.InvalidNodeCount, corruptNode ? Is.GreaterThan(0) : Is.Zero);
            Assert.That(report.RootMatches, Is.EqualTo(corruptNode));
            Assert.That(report.IsValid, Is.False);
        }
    }

    [Test]
    public async Task Scanner_rejects_missing_or_mismatched_referenced_code([Values] bool missing)
    {
        using SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtConfig config = new();
        PbtRocksDbPersistence persistence = new(db, config);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), default, WriteFlags.None))
        {
            batch.SetAccount(PbtKeyDerivation.AddressKeyHash(TestItem.AddressA), new Account(1, 100).WithChangedCodeHash(TestItem.KeccakB));
            batch.Commit();
        }
        if (!missing) db.GetColumnDb(PbtColumns.Codes)[TestItem.KeccakB.Bytes] = Bytes.FromHexString("0x6001600055");
        PbtScanReport report = await new PbtScanner(db, config, LimboLogs.Instance).Scan(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.InvalidLeafCount, Is.GreaterThan(0));
            Assert.That(report.IsValid, Is.False);
        }
    }

    [TestCase(PbtColumns.Accounts)]
    [TestCase(PbtColumns.Storages)]
    [TestCase(PbtColumns.Codes)]
    public async Task Scanner_reports_malformed_typed_entries(PbtColumns column)
    {
        PbtConfig config = new();
        SnapshotableMemColumnsDb<PbtColumns> db = new("pbt");
        PbtRocksDbPersistence persistence = new(db, config);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(
            StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), default, WriteFlags.None)) batch.Commit();
        byte[] key = column == PbtColumns.Storages
            ? PbtStateKey.Storage(TestItem.AddressA, 63).Bytes.ToArray()
            : TestItem.KeccakA.Bytes.ToArray();
        db.GetColumnDb(column)[key] = Bytes.FromHexString("0x01");

        PbtScanReport report = await new PbtScanner(db, config, LimboLogs.Instance).Scan(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.InvalidLeafCount, Is.EqualTo(1));
            Assert.That(report.IsValid, Is.False);
        }
    }

    [Test]
    public async Task Consecutive_storage_slots_share_one_stem()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA));
            // Slots 100 and 101 share a storage stem.
            batch.SetStorage(TestItem.AddressA, 100, SlotValue.FromSpanWithoutLeadingZero([0xAA]));
            batch.SetStorage(TestItem.AddressA, 101, SlotValue.FromSpanWithoutLeadingZero([0xBB]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        // A flush interval of 1 exercises same-stem merging across windows.
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);
        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        PbtScanReport report = await new PbtScanner(pbtDb, new PbtConfig(), LimboLogs.Instance).Scan(CancellationToken.None);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(report.LeafCount, Is.EqualTo(4), "two account leaves and two storage leaves must be present");
            Assert.That(report.InvalidLeafCount, Is.Zero);
            Assert.That(report.IsValid, Is.True, "persisted groups must match the reconstructed canonical nodes");
        }
    }

    /// <summary>
    /// Leaf chunks spanning parallel partitions and fold windows must produce the same tree across all zones.
    /// </summary>
    /// <param name="entryChunkSize">Number of leaves per channel chunk.</param>
    /// <param name="workers">Concurrent partition readers.</param>
    /// <param name="windowSize">Leaves per committed tree update.</param>
    [TestCase(1, 1, 1)]
    [TestCase(5, 3, 3)]
    [TestCase(2048, 3, 0)]
    public async Task Leaf_chunks_fold_to_the_same_root(int entryChunkSize, int workers, int windowSize)
    {
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetAccount(model, TestItem.AddressC, 9, 5, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        PbtReferenceModel.SetSlot(model, TestItem.AddressC, 2000, 0x55);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 70, SlotValue.FromSpanWithoutLeadingZero([0x07]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
            batch.SetAccount(TestItem.AddressC, new Account(9, 5).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakB));
            batch.SetStorage(TestItem.AddressC, 2000, SlotValue.FromSpanWithoutLeadingZero([0x55]));
        }

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        RecordingExitSource exitSource = new();
        PbtConfig config = new() { ImportStorageReadConcurrency = workers, ImportWindowSize = windowSize };
        ImportPbtFromPreimageFlat step = new(flatSource, codeDb, pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, config, exitSource, LimboLogs.Instance)
        {
            EntryChunkSize = entryChunkSize,
        };

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "leaf chunks must fold to the same root");
        Assert.That(reader.GetCodeReference(bigCodeHash.ValueHash256), Is.EqualTo(2));
        PbtScanReport report = await new PbtScanner(pbtDb, config, LimboLogs.Instance).Scan(CancellationToken.None);
        Assert.That(report.IsValid, Is.True, "all persisted groups must match the canonical reconstruction");
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressC, 2000)).ToArray(), Is.EqualTo(((UInt256)0x55).ToBigEndian()));
    }

    [Test]
    public async Task Importer_bypasses_cached_persistence_wrapper()
    {
        PbtConfig config = new();
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence rawPersistence = new(pbtDb, config);
        IPbtPersistence cachedWrapper = NSubstitute.Substitute.For<IPbtPersistence>();
        RecordingExitSource exitSource = new();

        ContainerBuilder builder = new();
        builder
            .AddSingleton<IPersistence>(flatSource)
            .AddKeyedSingleton<IDb>(DbNames.Code, new MemDb())
            .AddSingleton<IColumnsDb<PbtColumns>>(pbtDb)
            .AddSingleton<PbtRocksDbPersistence>(rawPersistence)
            .AddSingleton<IPbtPersistence>(rawPersistence)
            .AddDecorator<IPbtPersistence>((_, _) => cachedWrapper)
            .AddSingleton<IPbtConfig>(config)
            .AddSingleton<IProcessExitSource>(exitSource)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<PbtRebuilder>();
        builder.RegisterType<ImportPbtFromPreimageFlat>().WithAttributeFiltering();
        using IContainer container = builder.Build();

        ImportPbtFromPreimageFlat step = container.Resolve<ImportPbtFromPreimageFlat>();
        Assert.That(container.Resolve<IPbtPersistence>(), Is.SameAs(cachedWrapper));

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = rawPersistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
    }

    [Test]
    public async Task Skips_when_pbt_already_populated()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
        }

        // Ensure the persisted target state is not pre-genesis.
        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig());
        ValueHash256 existingRoot = new(Keccak.Compute("existing").Bytes);
        using (IPbtPersistence.IWriteBatch persisted = pbtTarget.CreateWriteBatch(StateId.PreGenesis, new StateId(1, existingRoot), default, WriteFlags.None))
            persisted.Commit();

        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.Null, "an already-populated target is skipped without exiting");
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(1, existingRoot)), "the existing state is left untouched");
    }

    [Test]
    public async Task Phase_two_ranges_cover_boundary_keys_once_and_overlap([Values(1, 3)] int workers)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None)) { }
        using RecordingColumnsDb pbtDb = new();
        PbtConfig config = new() { ImportStorageReadConcurrency = workers, ImportWindowSize = 3 };
        PbtRocksDbPersistence target = new(pbtDb, config);
        Dictionary<string, byte[]> model = [];
        HashSet<string> expectedRows = [];
        int partitionCount = workers * 16;
        using Barrier overlap = new(workers);
        int gatedViews = 0;
        int partitionViews = 0;
        pbtDb.ViewOpened = (column, start, end) =>
        {
            if (column == PbtColumns.Accounts && Interlocked.Increment(ref gatedViews) <= workers)
                Assert.That(overlap.SignalAndWait(TimeSpan.FromSeconds(20)), Is.True, "configured workers must enter separate range views concurrently");
            int prefixOffset = column == PbtColumns.Accounts ? 0 : 1;
            Assert.That(start.AsSpan().SequenceCompareTo(end), Is.LessThan(0));
            Assert.That(start.Length, Is.EqualTo(prefixOffset + 2).Or.EqualTo(column == PbtColumns.Accounts ? 33 : start[0] == 0 ? 35 : 67), "views begin at a partition boundary or immediately after the last complete key");
            if (start.Length == prefixOffset + 2)
            {
                Interlocked.Increment(ref partitionViews);
                int prefix = BinaryPrimitives.ReadUInt16BigEndian(start.AsSpan(prefixOffset));
                Assert.That(prefix, Is.EqualTo((long)((prefix * partitionCount + 65535) / 65536) * 65536 / partitionCount));
            }
        };
        pbtDb.AfterCopy = () =>
        {
            // Synthetic hashes reach exact partition edges that cannot feasibly be obtained from address preimages.
            using IPbtPersistence.IWriteBatch staging = target.CreateStagingWriteBatch(WriteFlags.None);
            HashSet<string> hashes = [];
            for (int partition = 0; partition <= partitionCount; partition++)
            {
                int boundary = (int)((long)partition * 65536 / partitionCount);
                foreach (int offset in new[] { -1, 0 })
                {
                    int prefix = boundary + offset;
                    if (prefix is < 0 or > 65535) continue;
                    byte[] hashBytes = new byte[32];
                    if (offset == -1) hashBytes.AsSpan().Fill(0xFF);
                    BinaryPrimitives.WriteUInt16BigEndian(hashBytes, (ushort)prefix);
                    if (!hashes.Add(Convert.ToHexString(hashBytes))) continue;
                    ValueHash256 hash = new(hashBytes);
                    Account account = new(1, 100);
                    staging.SetAccount(hash, account);
                    expectedRows.Add($"{PbtColumns.Accounts}:{Convert.ToHexString(hashBytes)}");
                    foreach ((PbtFullKey key, ValueHash256 value) in PbtFlatState.AccountLeaves(hash, account, null))
                        model[Convert.ToHexString(key.Bytes)] = value.Bytes.ToArray();
                    foreach (byte zone in new byte[] { 0, 0xFF })
                    {
                        byte[] storageKey = new byte[zone == 0 ? 34 : 66];
                        storageKey[0] = zone;
                        hashBytes.CopyTo(storageKey, 1);
                        storageKey[^1] = zone == 0 ? (byte)64 : (byte)0xFF;
                        if (zone == 0xFF) storageKey.AsSpan(33).Fill(0xFF);
                        ValueHash256 value = TestItem.KeccakA.ValueHash256;
                        staging.SetSlot(new PbtStorageFullKey(storageKey), EvmWordSlot.FromStripped(value.Bytes));
                        expectedRows.Add($"{PbtColumns.Storages}:{Convert.ToHexString(storageKey)}");
                        model[Convert.ToHexString(storageKey)] = value.Bytes.ToArray();
                    }
                }
            }
            staging.Commit();
        };
        RecordingExitSource exit = new();
        ImportPbtFromPreimageFlat step = new(flatSource, new MemDb(), pbtDb, new PbtRebuilder(target, LimboLogs.Instance), target, config, exit, LimboLogs.Instance) { EntryChunkSize = 1 };

        await step.Execute(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));

        HashSet<string> actualRows = [];
        foreach ((string key, int count) in pbtDb.Rows)
        {
            actualRows.Add(key);
            Assert.That(count, Is.EqualTo(1), "range ends and resumed pages cannot duplicate a row");
        }
        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit.ExitCode, Is.Zero);
            Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(actualRows, Is.EquivalentTo(expectedRows));
            Assert.That(pbtDb.ActiveViews, Is.Zero);
            Assert.That(partitionViews, Is.EqualTo(partitionCount * 3), "accounts and both storage zones each use disjoint partitions");
            Assert.That(pbtDb.GroupCommits, Is.GreaterThan(1));
        }
        pbtDb.Recording = false;
        PbtScanReport report = await new PbtScanner(pbtDb, config, LimboLogs.Instance).Scan(CancellationToken.None);
        Assert.That(report.IsValid, Is.True, "canonical node groups include both storage zones and the terminal all-FF key");
    }

    [TestCase("missing-code")]
    [TestCase("persistence")]
    [TestCase("cancellation")]
    public async Task Failed_phase_two_terminates_without_publication_and_retries(string failure)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence source = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        byte[] code = Bytes.FromHexString("0x6001600055");
        Hash256 codeHash = Keccak.Compute(code);
        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100, code);
        using (IPersistence.IWriteBatch batch = source.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100).WithChangedCodeHash(codeHash).WithChangedStorageRoot(TestItem.KeccakA));
            for (uint slot = 0; slot < 100; slot++)
            {
                batch.SetStorage(TestItem.AddressA, slot, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x01")));
                PbtReferenceModel.SetSlot(model, TestItem.AddressA, slot, 1);
            }
        }
        using MemDb codes = new();
        codes[codeHash.Bytes] = code;
        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ImportStorageReadConcurrency = 1, ImportWindowSize = 1, ImportFromPreimageFlat = true };
        PbtRocksDbPersistence target = new(db, config);
        using CancellationTokenSource cancellation = new();
        using ManualResetEventSlim releaseConsumer = new();
        TaskCompletionSource backpressurePageClosed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int storagePages = 0;
        db.ViewClosed = (column, rows) =>
        {
            if (column == PbtColumns.Storages && rows != 0 && Interlocked.Increment(ref storagePages) == 63)
                backpressurePageClosed.TrySetResult();
        };
        db.AfterCopy = () =>
        {
            if (failure == "missing-code") db.GetColumnDb(PbtColumns.Codes).Remove(codeHash.Bytes);
        };
        db.AfterGroupCommit = () =>
        {
            if (db.GroupCommits != 1) return;
            if (!releaseConsumer.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Consumer gate was not released.");
            if (failure == "persistence") throw new IOException("injected staging persistence failure");
        };
        RecordingExitSource exit = new();
        ImportPbtFromPreimageFlat step = new(source, codes, db, new PbtRebuilder(target, LimboLogs.Instance), target, config, exit, LimboLogs.Instance) { EntryChunkSize = 1 };
        Task import = step.Execute(cancellation.Token);
        try
        {
            if (failure != "missing-code")
            {
                // Three account/code leaves plus 63 storage leaves exceed the consumed leaf and 64 queued chunks.
                await backpressurePageClosed.Task.WaitAsync(TimeSpan.FromSeconds(30));
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(import.IsCompleted, Is.False);
                    Assert.That(db.ActiveViews, Is.Zero, "the page must close before its channel write blocks");
                    Assert.That(db.GroupCommits, Is.EqualTo(1));
                    Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("currentState"u8), Is.Null);
                }
                if (failure == "cancellation") await cancellation.CancelAsync();
            }
        }
        finally
        {
            releaseConsumer.Set();
        }
        if (failure == "cancellation") await import.WaitAsync(TimeSpan.FromSeconds(30));
        else
        {
            Exception? error = Assert.CatchAsync(async () => await import.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.That(error, failure == "missing-code"
                ? Is.TypeOf<InvalidDataException>().With.Message.Contains("Missing staged bytecode")
                : Is.TypeOf<IOException>().With.Message.Contains("injected staging persistence failure"));
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("validState"u8), Is.Null);
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("currentState"u8), Is.Null);
            Assert.That(db.ActiveViews, Is.Zero);
            Assert.That(exit.ExitCode, failure == "cancellation" ? Is.EqualTo(1) : Is.Null);
        }
        db.AfterCopy = null;
        db.AfterGroupCommit = null;
        db.ViewClosed = null;
        db.Recording = false;
        RecordingExitSource retryExit = new();
        ImportPbtFromPreimageFlat retry = new(source, codes, db, new PbtRebuilder(target, LimboLogs.Instance), target, config, retryExit, LimboLogs.Instance) { EntryChunkSize = 5, ClearKeyChunk = 1 };
        await retry.Execute(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(retryExit.ExitCode, Is.Zero);
            Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(reader.GetCodeReference(codeHash.ValueHash256), Is.EqualTo(1));
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
        }
    }

    private sealed class RecordingColumnsDb : IColumnsDb<PbtColumns>
    {
        private readonly SnapshotableMemColumnsDb<PbtColumns> _database = new("pbt");
        private readonly Dictionary<PbtColumns, IDb> _columns = [];
        public readonly ConcurrentDictionary<string, int> Rows = new();
        public Action? AfterCopy;
        public Action? AfterGroupCommit;
        public Action<PbtColumns, byte[], byte[]>? ViewOpened;
        public Action<PbtColumns, int>? ViewClosed;
        public bool Recording;
        public int ActiveViews;
        public int GroupCommits;
        private int _flushed;

        public RecordingColumnsDb()
        {
            foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
                _columns[column] = new RecordingDb(this, column, _database.GetColumnDb(column));
        }

        public IDb GetColumnDb(PbtColumns key) => _columns[key];
        public IEnumerable<PbtColumns> ColumnKeys => _database.ColumnKeys;
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot() => _database.CreateSnapshot();
        public IColumnsWriteBatch<PbtColumns> StartWriteBatch() => new RecordingBatch(this, _database.StartWriteBatch());
        public void Dispose() => _database.Dispose();
        public void Flush(bool onlyWal = false)
        {
            _database.Flush(onlyWal);
            if (!onlyWal && Interlocked.Exchange(ref _flushed, 1) == 0)
            {
                AfterCopy?.Invoke();
                Recording = true;
            }
        }

        private sealed class RecordingBatch(RecordingColumnsDb owner, IColumnsWriteBatch<PbtColumns> batch) : IColumnsWriteBatch<PbtColumns>
        {
            private bool _groups;
            public IWriteBatch GetColumnBatch(PbtColumns key)
            {
                if (key == PbtColumns.NodeGroups && owner.Recording) _groups = true;
                return batch.GetColumnBatch(key);
            }
            public void Clear()
            {
                _groups = false;
                batch.Clear();
            }
            public void Dispose()
            {
                batch.Dispose();
                if (_groups)
                {
                    Interlocked.Increment(ref owner.GroupCommits);
                    owner.AfterGroupCommit?.Invoke();
                }
            }
        }

        private sealed class RecordingDb(RecordingColumnsDb owner, PbtColumns column, IDb database) : IDb, ISortedKeyValueStore
        {
            private ISortedKeyValueStore Sorted => (ISortedKeyValueStore)database;
            public string Name => database.Name;
            public byte[]? FirstKey => Sorted.FirstKey;
            public byte[]? LastKey => Sorted.LastKey;
            public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys] => database[keys];
            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => database.Get(key, flags);
            public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None) => database.Set(key, value, flags);
            public IEnumerable<KeyValuePair<byte[], byte[]>> GetAll(bool ordered = false) => database.GetAll(ordered);
            public IEnumerable<byte[]> GetAllKeys(bool ordered = false) => database.GetAllKeys(ordered);
            public IEnumerable<byte[]> GetAllValues(bool ordered = false) => database.GetAllValues(ordered);
            public IWriteBatch StartWriteBatch() => database.StartWriteBatch();
            public void Flush(bool onlyWal = false) => database.Flush(onlyWal);
            public void Dispose() { }
            public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None)
            {
                ISortedView view = Sorted.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags);
                if (!owner.Recording || column is not (PbtColumns.Accounts or PbtColumns.Storages)) return view;
                Interlocked.Increment(ref owner.ActiveViews);
                RecordingView recordingView = new(owner, column, view);
                try
                {
                    owner.ViewOpened?.Invoke(column, firstKeyInclusive.ToArray(), lastKeyExclusive.ToArray());
                    return recordingView;
                }
                catch
                {
                    recordingView.Dispose();
                    throw;
                }
            }
        }

        private sealed class RecordingView(RecordingColumnsDb owner, PbtColumns column, ISortedView view) : ISortedView
        {
            private int _rows;
            public ReadOnlySpan<byte> CurrentKey => view.CurrentKey;
            public ReadOnlySpan<byte> CurrentValue => view.CurrentValue;
            public bool StartBefore(ReadOnlySpan<byte> value) => view.StartBefore(value);
            public bool MoveNext()
            {
                if (!view.MoveNext()) return false;
                _rows++;
                owner.Rows.AddOrUpdate($"{column}:{Convert.ToHexString(view.CurrentKey)}", 1, static (_, count) => count + 1);
                return true;
            }
            public void Dispose()
            {
                view.Dispose();
                Interlocked.Decrement(ref owner.ActiveViews);
                owner.ViewClosed?.Invoke(column, _rows);
            }
        }
    }

    private sealed class RecordingExitSource : IProcessExitSource
    {
        public int? ExitCode { get; private set; }
        public CancellationToken Token => CancellationToken.None;
        public void Exit(int exitCode) => ExitCode ??= exitCode;
    }
}
