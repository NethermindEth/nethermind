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
    [Test]
    public async Task Imports_preimage_flat_state_into_pbt_and_exits([Values(0, 1, 3)] int windowSize, [Values(5, 8000)] int codeLength)
    {
        PbtConfig config = new() { ImportWindowSize = windowSize };
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger progressLogger = new(logger);
        logs.GetClassLogger<ProgressLogger>().Returns(progressLogger);

        byte[] bigCode = new byte[codeLength];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);      // header-region slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);     // storage-zone slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        // A second contract with the same code exercises content-addressed chunk deduplication.
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
        ImportPbtFromPreimageFlat step = new(flatSource, codeDb, pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance), pbtTarget, config, exitSource, logs);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)), "the state is keyed by the source's header root");
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "with the folded tree's own root recorded beside it");
        foreach (string partitionName in new[] { "accounts/code", "header storage", "overflow storage" })
        {
            logger.Received().Info(Arg.Is<string>(message => message.StartsWith($"PBT import phase 2 {partitionName}: 0.00 % ")));
            logger.Received().Info(Arg.Is<string>(message => message.StartsWith($"PBT import phase 2 {partitionName}: 100.00 % ")));
        }
        Assert.That(reader.GetCodeReference(bigCodeHash.ValueHash256), Is.EqualTo(2), "shared code references survive later account changes");
        PbtScanReport scan = await new PbtScanner(pbtDb, config, LimboLogs.Instance).Scan(CancellationToken.None);
        Assert.That(scan.Accounts.RecordCount, Is.EqualTo(3), scan.Format());
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressC)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(reader.GetCode(bigCodeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(bigCode));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!.StorageRoot, Is.EqualTo(TestItem.KeccakA));
        Assert.That(pbtDb.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty);
        Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));

        PbtRocksDbPersistence reopened = new(pbtDb, config);
        PbtResourcePool pool = new(config);
        using PbtSnapshotBundle bundle = new(new PbtSnapshotPooledList(0),
            new PbtReadOnlySnapshotBundle(new PbtSnapshotPooledList(0), reopened.CreateReader()), pool, PbtResourcePool.Usage.MainBlockProcessing);
        Account retained = bundle.GetAccount(TestItem.AddressB)!.WithChangedNonce(4).WithChangedBalance(43);
        bundle.SetAccount(TestItem.AddressB, retained);
        bundle.SetAccount(TestItem.AddressC, null);
        Assert.That(bundle.GetCodeReference(bigCodeHash.ValueHash256), Is.EqualTo(1));
        int codeLeaves = 0;
        foreach ((PbtStorageFullKey key, ValueHash256 _) in bundle.EnumerateLeaves())
            if (key.Bytes[0] == 0x01) codeLeaves++;
        int expectedCodeLeaves = 0;
        foreach (string key in model.Keys)
            if (key.StartsWith("01", StringComparison.Ordinal)) expectedCodeLeaves++;
        Assert.That(codeLeaves, Is.EqualTo(expectedCodeLeaves), "zero chunks remain absent after reopening");
        bundle.SetAccount(TestItem.AddressB, null);
        using PbtPartitionBatches changes = bundle.PrepareLeafChanges();
        ValueHash256 remainingRoot = TrieUpdater.UpdateRoot(new PbtSnapshotStore(bundle), reader.CurrentRoot, changes);
        bundle.CompleteLeafChanges();
        model.Clear();
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.GetCodeReference(bigCodeHash.ValueHash256), Is.Zero);
            Assert.That(remainingRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "last-owner deletion must remove all persisted code chunks");
        }
    }

    [Test]
    public async Task Phase_two_progress_tracks_scanned_paths_before_partition_completion(
        [Values(0, 1, 2)] int zone,
        [Values(0, 15)] int partition,
        [Values(1, 2)] int pages)
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence source = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using (IPersistence.IWriteBatch batch = source.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None)) { }
        using MemDb codes = new();
        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ImportStorageReadConcurrency = 1 };
        PbtRocksDbPersistence target = new(db, config);
        using CancellationTokenSource cancellation = new();
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger progressLogger = new(logger);
        logs.GetClassLogger<ProgressLogger>().Returns(progressLogger);
        int prefixOffset = zone == 0 ? 0 : 1;
        PbtColumns column = zone == 0 ? PbtColumns.Accounts : PbtColumns.Storages;
        byte zoneByte = zone == 2 ? (byte)0xFF : (byte)0;
        db.AfterCopy = () =>
        {
            for (int page = 1; page <= 2; page++)
            {
                byte[] key = new byte[zone == 0 ? 32 : zone == 1 ? 34 : 66];
                if (zone != 0) key[0] = zoneByte;
                key[prefixOffset] = (byte)(partition * 16 + page * 4);
                key[prefixOffset + 1] = 3;
                key[prefixOffset + 2] = 0xFF;
                if (zone != 0) key[^1] = PbtKeyDerivation.HeaderStorageOffset;
                byte[] value = zone == 0
                    ? Nethermind.Serialization.Rlp.Rlp.Encode(new Account(1, 100)).Bytes
                    : TestItem.KeccakA.Bytes.ToArray();
                db.GetColumnDb(column).Set(key, value);
            }
        };
        int resumedPages = 0;
        db.ViewOpened = (scannedColumn, start, _) =>
        {
            if (scannedColumn == column && start.Length > prefixOffset + 2 &&
                (zone == 0 || start[0] == zoneByte) && ++resumedPages == pages)
                cancellation.Cancel();
        };
        RecordingExitSource exit = new();
        ImportPbtFromPreimageFlat step = new(source, codes, db, new PbtRebuilder(target, LimboLogs.Instance), target, config, exit, logs) { EntryChunkSize = 1 };

        await step.Execute(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(30));

        string zoneName = zone switch { 0 => "accounts/code", 1 => "header storage", _ => "overflow storage" };
        double scanned = (partition * 16 + pages * 4 + 3 / 256.0 + 0xFF / 65536.0) / 256;
        string percentage = scanned.ToString("P2", System.Globalization.CultureInfo.InvariantCulture);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumedPages, Is.EqualTo(pages));
            Assert.That(exit.ExitCode, Is.EqualTo(1));
            Assert.That(db.ActiveViews, Is.Zero);
            Assert.That(target.IsValid, Is.False);
            logger.Received().Info(Arg.Is<string>(message => message.StartsWith($"PBT import phase 2 {zoneName}: 0.00 % ")));
            logger.Received().Info(Arg.Is<string>(message => message.StartsWith($"PBT import phase 2 {zoneName}: {percentage} ")));
            logger.DidNotReceive().Info(Arg.Is<string>(message => message.StartsWith($"PBT import phase 2 {zoneName}: 100.00 % ")));
        }
    }

    [TestCase(1, 101)]
    [TestCase(3, 37)]
    [TestCase(17, 0)]
    public async Task Phase_one_bounds_batches_and_preserves_state(int accountCount, int slotsPerAccount)
    {
        const int batchSize = 7;
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence source = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat);
        using MemDb codes = new();
        byte[] code = Bytes.FromHexString("0x6001600055");
        Hash256 codeHash = Keccak.Compute(code);
        codes[codeHash.Bytes] = code;
        Dictionary<string, byte[]> model = [];
        List<Address> addresses = [];
        using (IPersistence.IWriteBatch batch = source.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, SourceStateRoot), WriteFlags.None))
        {
            for (int accountIndex = 0; accountIndex < accountCount; accountIndex++)
            {
                byte[] addressBytes = new byte[20];
                addressBytes[^1] = (byte)accountIndex;
                Address address = new(addressBytes);
                addresses.Add(address);
                Account account = new Account(1, 100).WithChangedCodeHash(codeHash);
                if (slotsPerAccount > 0) account = account.WithChangedStorageRoot(TestItem.KeccakA);
                batch.SetAccount(address, account);
                PbtReferenceModel.SetAccount(model, address, 1, 100, code);
                for (uint slot = 0; slot < slotsPerAccount; slot++)
                {
                    batch.SetStorage(address, slot, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x01")));
                    PbtReferenceModel.SetSlot(model, address, slot, 1);
                }
            }
        }

        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ImportStorageReadConcurrency = 1 };
        PbtRocksDbPersistence target = new(db, config);
        RecordingExitSource exit = new();
        List<int> copyBatchWrites = [];
        db.AfterCopyBatch = copyBatchWrites.Add;
        db.AfterCopy = () =>
        {
            using IPbtPersistence.IReader staged = target.CreateReader();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(staged.CurrentState, Is.EqualTo(StateId.PreGenesis));
                Assert.That(target.IsValid, Is.False);
            }
        };
        ImportPbtFromPreimageFlat step = new(source, codes, db, new PbtRebuilder(target, LimboLogs.Instance), target, config, exit, LimboLogs.Instance) { CopyBatchSize = batchSize };

        await step.Execute(CancellationToken.None);

        using IPbtPersistence.IReader reader = target.CreateReader();
        int expectedWrites = accountCount * (slotsPerAccount + 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit.ExitCode, Is.Zero);
            Assert.That(copyBatchWrites.Count, Is.EqualTo((expectedWrites + batchSize - 1) / batchSize));
            Assert.That(copyBatchWrites, Has.All.InRange(1, batchSize));
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
            Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(reader.GetCode(codeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(code));
            Assert.That(reader.GetCodeReference(codeHash.ValueHash256), Is.EqualTo(accountCount));
            foreach (Address address in addresses)
            {
                Assert.That(PbtTestLeaves.ReadAccount(reader, address)!.Balance, Is.EqualTo((UInt256)100));
                for (uint slot = 0; slot < slotsPerAccount; slot++)
                    Assert.That(EvmWordSlot.AsReadOnlySpan(PbtTestLeaves.ReadSlot(reader, address, slot)).ToArray(), Is.EqualTo(UInt256.One.ToBigEndian()));
            }
        }
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
    public async Task Import_mode_recovers_an_interrupted_epoch_12_attempt(int clearKeyChunk)
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

        using RecordingColumnsDb pbtDb = new();
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

        PbtColumns[] groupColumns = [PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups];
        foreach (PbtColumns column in groupColumns)
            pbtDb.GetColumnDb(column)[maximumLengthKey] = Bytes.FromHexString("0x7f");
        pbtDb.AfterCopy = () =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(pbtDb.GetColumnDb(PbtColumns.Metadata).Get("rootNodeGroup"u8), Is.Null, "the stale root must be removed before folding");
                foreach (PbtColumns column in groupColumns)
                    Assert.That(pbtDb.GetColumnDb(column).GetAll(), Is.Empty, column.ToString());
            }
        };

        IDb metadata = pbtDb.GetColumnDb(PbtColumns.Metadata);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Get("schemaEpoch"u8), Is.EqualTo(Bytes.FromHexString("0x0000000c")));
            Assert.That(metadata.Get("rootNodeGroup"u8), Is.Not.Null);
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
            Assert.That(metadata.Get("rootNodeGroup"u8), Is.Not.Null);
            foreach (PbtColumns column in groupColumns)
                Assert.That(pbtDb.GetColumnDb(column).Get(maximumLengthKey), Is.Null, column.ToString());
            Assert.That(pbtDb.GetColumnDb(PbtColumns.FullLeaves).GetAll(), Is.Empty, "import must not populate a split-leaf column");
            Assert.That(pbtDb.GetColumnDb(PbtColumns.Storages).Get(maximumLengthKey), Is.Null, "the full keyspace must be cleared during retry");
            Assert.That(() => new PbtRocksDbPersistence(pbtDb, new PbtConfig()), Throws.Nothing);
        }
    }

    [Test]
    public async Task Scanner_counts_stored_records_without_integrity_checks([Values(0, 1, 2)] int concurrency)
    {
        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ScanTreeConcurrency = concurrency };
        PbtRocksDbPersistence persistence = new(db, config);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), TestItem.KeccakC.ValueHash256, WriteFlags.None)) batch.Commit();
        Dictionary<PbtColumns, (long Count, long Keys, long Values)> expected = [];
        HashSet<string> expectedRows = [];
        void Add(PbtColumns column, byte[] key, byte[] value)
        {
            db.GetColumnDb(column).Set(key, value);
            expected.TryGetValue(column, out (long Count, long Keys, long Values) totals);
            expected[column] = (totals.Count + 1, totals.Keys + key.Length, totals.Values + value.Length);
            expectedRows.Add($"{column}:{Convert.ToHexString(key)}");
        }
        foreach (byte prefix in new byte[] { 0, 8, 128, 255 })
        {
            byte[] key = new byte[32];
            Array.Fill(key, prefix);
            Add(PbtColumns.Accounts, key, Nethermind.Serialization.Rlp.Rlp.Encode(new Account(1, 100).WithChangedCodeHash(TestItem.KeccakB)).Bytes);
            Add(PbtColumns.Codes, key, Bytes.FromHexString("0x6001600055"));
            foreach (byte zone in new[] { Eip8297KeyDerivation.AccountZone, Eip8297KeyDerivation.StorageZone })
            {
                byte[] storage = new byte[zone == Eip8297KeyDerivation.AccountZone ? 34 : 66];
                Array.Fill(storage, prefix);
                storage[0] = zone;
                if (zone == Eip8297KeyDerivation.AccountZone) storage[^1] = PbtKeyDerivation.HeaderStorageOffset;
                Add(PbtColumns.Storages, storage, TestItem.KeccakA.Bytes.ToArray());
            }
        }
        Add(PbtColumns.Codes, TestItem.KeccakB.Bytes.ToArray(), Bytes.FromHexString("0x01"));
        Add(PbtColumns.Accounts, TestItem.KeccakC.Bytes.ToArray(), Nethermind.Serialization.Rlp.Rlp.Encode(new Account(1, 100).WithChangedCodeHash(TestItem.KeccakA)).Bytes);
        long[] expectedGroups = new long[PbtFourLevelGroupGeometry.MaxPathDepth + 1];
        long[] expectedNodes = new long[expectedGroups.Length];
        long[] expectedPayloads = new long[expectedGroups.Length];
        long encodingBytes = 0;
        (int Depth, byte Prefix, PbtColumns Column)[] groups =
        [
            (0, 0, PbtColumns.Metadata),
            (4, 0, PbtColumns.AccountNodeGroups),
            (4, 0xF0, PbtColumns.StorageNodeGroups),
            (8, 0, PbtColumns.AccountNodeGroups),
            (8, 1, PbtColumns.CodeNodeGroups),
            (8, 0xFF, PbtColumns.StorageNodeGroups),
            (32, 0x80, PbtColumns.AccountNodeGroups),
            (PbtFourLevelGroupGeometry.MaxGroupDepth, 0xFF, PbtColumns.StorageNodeGroups),
        ];
        foreach ((int depth, byte prefix, PbtColumns column) in groups)
        {
            byte[] pathBytes = new byte[(depth + 7) / 8];
            Array.Fill(pathBytes, byte.MaxValue);
            if (pathBytes.Length != 0) pathBytes[0] = prefix;
            if (depth % 8 != 0) pathBytes[^1] &= 0xF0;
            IPbtNodePath group = PbtPathOperations.Create(pathBytes, depth);
            IPbtNodePath node = depth == 0 ? group : PbtFourLevelGroupGeometry.PathOf(group, 0);
            byte[] keyBytes = new byte[PbtStorageFullKey.MaxLength];
            node.Path.CopyTo(keyBytes);
            byte[] encoding = depth == 0
                ? PbtNodeCodec.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256)
                : PbtNodeCodec.EncodeLeaf(new PbtStorageFullKey(keyBytes), TestItem.KeccakA.Bytes);
            BufferWriter writer = new(new byte[1024]);
            PbtNodeGroupCodec.Encode(ref writer, group, new[] { new PbtNodeRecord(node, encoding) });
            Add(column, depth == 0 ? "rootNodeGroup"u8.ToArray() : group.Encode(), writer.WrittenSpan.ToArray());
            expectedGroups[depth]++;
            expectedPayloads[depth] += writer.WrittenSpan.Length;
            expectedNodes[node.BitDepth]++;
            encodingBytes += encoding.Length;
        }
        db.Recording = true;
        db.RecordAllColumns = true;
        db.ForbidPointReads = true;
        PbtScanReport report = await new PbtScanner(db, config, LimboLogs.Instance).Scan(CancellationToken.None);
        string formatted = report.Format();
        using (Assert.EnterMultipleScope())
        {
            foreach ((PbtColumns column, (long count, long keys, long values)) in expected)
            {
                Assert.That(report[column].RecordCount, Is.EqualTo(count), column.ToString());
                Assert.That(report[column].KeyBytes, Is.EqualTo(keys));
                Assert.That(report[column].ValueBytes, Is.EqualTo(values));
                Assert.That(report[column].TotalBytes, Is.EqualTo(keys + values));
                Assert.That(report[column].AverageRecordBytes, Is.EqualTo((double)(keys + values) / count));
                string label = column == PbtColumns.Metadata ? "Metadata (root only)" : column.ToString();
                Assert.That(formatted, Does.Contain($"  {label,-20} {count,15:N0} {keys,18:N0} {values,18:N0} {keys + values,18:N0} {(double)(keys + values) / count,12:N1}"));
            }
            HashSet<string> actualRows = [];
            foreach ((string key, int count) in db.Rows)
            {
                actualRows.Add(key);
                Assert.That(count, Is.EqualTo(1), key);
            }
            Assert.That(actualRows, Is.EquivalentTo(expectedRows));
            Assert.That(db.ActiveViews, Is.Zero);
            Assert.That(report.NodeGroups.GroupsByDepth, Is.EqualTo(expectedGroups));
            Assert.That(report.NodeGroups.PayloadBytesByDepth, Is.EqualTo(expectedPayloads));
            Assert.That(report.NodeGroups.NodesByDepth, Is.EqualTo(expectedNodes));
            Assert.That(report.NodeGroups.NodeCount, Is.EqualTo(groups.Length));
            Assert.That(report.NodeGroups.LeafCount, Is.EqualTo(groups.Length - 1));
            Assert.That(report.NodeGroups.BranchCount, Is.EqualTo(1));
            Assert.That(report.NodeGroups.GroupsByOccupancy[1], Is.EqualTo(groups.Length));
            Assert.That(report.NodeGroups.NodeEncodingBytes, Is.EqualTo(encodingBytes));
            Assert.That(report.NodeGroups.RecordCount, Is.EqualTo(groups.Length));
            long groupKeyBytes = 0, groupValueBytes = 0;
            foreach (PbtColumns column in new[] { PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups, PbtColumns.Metadata })
            {
                groupKeyBytes += expected[column].Keys;
                groupValueBytes += expected[column].Values;
            }
            Assert.That(report.NodeGroups.KeyBytes, Is.EqualTo(groupKeyBytes));
            Assert.That(report.NodeGroups.ValueBytes, Is.EqualTo(groupValueBytes));
            Assert.That(formatted, Does.Contain("not hash or reachability verification"));
            Assert.That(formatted, Does.Contain("Node groups and contained nodes by bit depth"));
            Assert.That(formatted, Does.Contain("Node-group occupancy"));
        }
    }

    [Test]
    public async Task Scanner_workers_overlap_and_release_views([Values("complete", "cancel", "failure")] string outcome)
    {
        using RecordingColumnsDb db = new() { Recording = true, RecordAllColumns = true, ForbidPointReads = true };
        db.GetColumnDb(PbtColumns.Accounts).Set(new byte[32], Bytes.FromHexString("0x01"));
        byte[] secondKey = new byte[32];
        secondKey[0] = 8;
        db.GetColumnDb(PbtColumns.Accounts).Set(secondKey, Bytes.FromHexString("0x02"));
        using CancellationTokenSource cancellation = new();
        using CountdownEvent opened = new(2);
        int arrivals = 0;
        db.ViewOpened = (column, _, _) =>
        {
            if (column != PbtColumns.Accounts || Interlocked.Increment(ref arrivals) > 2) return;
            opened.Signal();
            if (!opened.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Two scan workers did not overlap.");
            if (outcome == "cancel") cancellation.Cancel();
            if (outcome == "failure") throw new IOException("injected range failure");
        };
        Task<PbtScanReport> scan = new PbtScanner(db, new PbtConfig { ScanTreeConcurrency = 2 }, LimboLogs.Instance).Scan(cancellation.Token);
        if (outcome == "complete")
        {
            PbtScanReport report = await scan.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.That(report.Accounts.RecordCount, Is.EqualTo(2));
            Assert.That(report.Storages.RecordCount, Is.Zero);
            Assert.That(report.Storages.AverageRecordBytes, Is.Zero);
        }
        else Assert.That(async () => await scan.WaitAsync(TimeSpan.FromSeconds(60)), outcome == "cancel" ? Throws.InstanceOf<OperationCanceledException>() : Throws.InstanceOf<IOException>());
        Assert.That(arrivals, Is.GreaterThanOrEqualTo(2));
        Assert.That(db.ActiveViews, Is.Zero);
    }

    [Test]
    public async Task Scanner_reports_completed_columns([Values] bool periodic)
    {
        using RecordingColumnsDb db = new() { Recording = true, RecordAllColumns = true };
        db.GetColumnDb(PbtColumns.Accounts).Set(TestItem.KeccakA.Bytes, Bytes.FromHexString("0x01"));
        using ManualResetEventSlim progressLogged = new();
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsInfo.Returns(true);
        ILogManager logs = Substitute.For<ILogManager>();
        ILogger scanLogger = new(logger);
        logs.GetClassLogger<PbtScanner>().Returns(scanLogger);
        logger.When(log => log.Info(Arg.Is<string>(message => message.Contains("PBT scan Accounts:") && !message.Contains("(completed)"))))
            .Do(_ => progressLogged.Set());
        if (periodic)
            db.ViewOpened = (column, _, _) =>
            {
                if (column == PbtColumns.Accounts && !progressLogged.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("No periodic scan progress was logged.");
            };
        PbtScanReport report = await new PbtScanner(db, new PbtConfig { ScanTreeConcurrency = 2 }, logs).Scan(CancellationToken.None);
        foreach (PbtColumns column in new[] { PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups })
            logger.Received().Info(Arg.Is<string>(message => message.Contains($"PBT scan {column}:") && message.Contains("(completed)")));
        Assert.That(report.Accounts.RecordCount, Is.EqualTo(1), "flat rows are counted without RLP decoding");
        if (periodic) Assert.That(progressLogged.IsSet, Is.True);
        Assert.That(db.ActiveViews, Is.Zero);
    }

    [Test]
    public async Task Scanner_startup_outcomes([Values("empty", "complete", "cancel", "malformed-root", "malformed-account", "malformed-code", "malformed-storage")] string outcome)
    {
        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ScanTreeConcurrency = 2 };
        PbtRocksDbPersistence persistence = new(db, config);
        if (outcome != "empty")
            using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), TestItem.KeccakB.ValueHash256, WriteFlags.None)) batch.Commit();
        bool malformed = outcome.StartsWith("malformed", StringComparison.Ordinal);
        if (malformed)
        {
            PbtColumns column = outcome switch
            {
                "malformed-root" => PbtColumns.Metadata,
                "malformed-account" => PbtColumns.AccountNodeGroups,
                "malformed-code" => PbtColumns.CodeNodeGroups,
                _ => PbtColumns.StorageNodeGroups,
            };
            byte prefix = column == PbtColumns.CodeNodeGroups ? (byte)1 : column == PbtColumns.StorageNodeGroups ? (byte)0xFF : (byte)0;
            byte[] key = column == PbtColumns.Metadata ? "rootNodeGroup"u8.ToArray() : new PbtNodePath([prefix], 8).Encode();
            db.GetColumnDb(column).Set(key, Bytes.FromHexString("0x7f"));
        }
        db.Recording = true;
        db.RecordAllColumns = true;
        using CancellationTokenSource cancellation = new();
        if (outcome == "cancel") cancellation.Cancel();
        RecordingExitSource exit = new();
        ScanPbtTree step = new(new PbtScanner(db, config, LimboLogs.Instance), persistence, exit, LimboLogs.Instance);
        if (malformed) Assert.That(async () => await step.Execute(cancellation.Token), Throws.InstanceOf<InvalidDataException>());
        else await step.Execute(cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exit.ExitCode, malformed ? Is.Null : Is.EqualTo(outcome == "cancel" ? 1 : 0));
            Assert.That(db.ActiveViews, Is.Zero);
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
            Assert.That(report.Accounts.RecordCount, Is.EqualTo(1));
            Assert.That(report.Storages.RecordCount, Is.EqualTo(2));
            Assert.That(report.NodeGroups.LeafCount, Is.EqualTo(4));
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
        Assert.That(report.Accounts.RecordCount, Is.EqualTo(3));
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
        Assert.That(report.NodeGroups.RecordCount, Is.GreaterThan(0));
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
        public Action<int>? AfterCopyBatch;
        public Action? AfterGroupCommit;
        public Action<PbtColumns, byte[], byte[]>? ViewOpened;
        public Action<PbtColumns, int>? ViewClosed;
        public bool Recording;
        public bool RecordAllColumns;
        public bool ForbidPointReads;
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
            private int _copyWrites;
            public IWriteBatch GetColumnBatch(PbtColumns key)
            {
                IWriteBatch columnBatch = batch.GetColumnBatch(key);
                if (!owner.Recording && key is PbtColumns.Accounts or PbtColumns.Storages or PbtColumns.Codes)
                    return new RecordingCopyBatch(this, columnBatch);
                if (key == PbtColumns.Metadata) return new RecordingMetadataBatch(owner, this, columnBatch);
                if (key is PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups && owner.Recording) _groups = true;
                return columnBatch;
            }
            private sealed class RecordingCopyBatch(RecordingBatch ownerBatch, IWriteBatch batch) : IWriteBatch
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    if (flags.HasFlag(WriteFlags.DisableWAL)) ownerBatch._copyWrites++;
                    batch.Set(key, value, flags);
                }
                public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => batch.Merge(key, value, flags);
                public void Clear() => batch.Clear();
                public void Dispose() => batch.Dispose();
            }

            private sealed class RecordingMetadataBatch(RecordingColumnsDb owner, RecordingBatch ownerBatch, IWriteBatch metadata) : IWriteBatch
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    if (key.SequenceEqual("rootNodeGroup"u8) && owner.Recording) ownerBatch._groups = true;
                    metadata.Set(key, value, flags);
                }
                public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => metadata.Merge(key, value, flags);
                public void Clear() => metadata.Clear();
                public void Dispose() => metadata.Dispose();
            }

            public void Clear()
            {
                _groups = false;
                _copyWrites = 0;
                batch.Clear();
            }
            public void Dispose()
            {
                batch.Dispose();
                if (_copyWrites > 0) owner.AfterCopyBatch?.Invoke(_copyWrites);
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
            public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => owner.ForbidPointReads ? throw new InvalidOperationException("Scanner must not perform point reads.") : database.Get(key, flags);
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
                if (!owner.Recording || (!owner.RecordAllColumns && column is not (PbtColumns.Accounts or PbtColumns.Storages))) return view;
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
