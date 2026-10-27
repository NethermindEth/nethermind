// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Pbt;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Common;
using Nethermind.State.Pbt.Image;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Persistence.TrieNodeLog;
using Nethermind.State.Pbt.Snapshot;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;
using static Nethermind.State.Pbt.Test.PbtReferenceModel;
using NSubstitute;

namespace Nethermind.State.Pbt.Test;

public class ImportPbtFromPreimageFlatTests
{
    private const ulong SourceBlock = 7;

    [Test]
    public void Code_reassembly_writes_each_distinct_code_without_db_reads(
        [Values(4, PbtLeafIngestion.BatchSize + 2, 1000)] int distinctCodes, [Values(1, 3)] int concurrency)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        Directory.CreateDirectory(scratch.Path);
        using RecordingColumnsDb db = new() { TrackCodeRebuild = true };
        PbtRocksDbPersistence target = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        // Each code is named by four accounts; the last two span several chunk stems and hold no nonzero chunk.
        List<byte[]> codes = [.. Enumerable.Range(0, distinctCodes).Select(RebuildTestCode),
            Enumerable.Range(0, 20_000).Select(static index => (byte)index).ToArray(), new byte[10_000]];
        List<RebuildEntry> leaves = CodeAccountLeaves([.. codes, .. codes, .. codes, .. codes]);

        StageAndReassemble(target, scratch.Path, leaves, concurrency, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.AccountsVisited, Is.Zero);
            Assert.That(db.CodeReads, Is.Zero);
            Assert.That(db.CodeWrites, Is.EqualTo(codes.Count));
            Assert.That(db.MaxActiveCodeWrites, Is.EqualTo(1));
            Assert.That(db.CodeBatchSizes, Is.EqualTo(Enumerable.Repeat(PbtLeafIngestion.BatchSize, codes.Count / PbtLeafIngestion.BatchSize)
                .Concat(codes.Count % PbtLeafIngestion.BatchSize == 0 ? [] : new[] { codes.Count % PbtLeafIngestion.BatchSize })));
            Assert.That(Directory.GetFileSystemEntries(scratch.Path), Is.Empty);
        }
        AssertCodeRebuildReleased(db, target);
        using IPbtPersistence.IReader reader = target.CreateReader();
        foreach (byte[] code in codes) Assert.That(reader.GetCode(ValueKeccak.Compute(code))!.Code.ToArray(), Is.EqualTo(code));
    }

    [Test]
    public void Code_reassembly_failure_releases_resources(
        [Values("spool-cancel", "write-cancel", "invalid-leaf", "zero-leaf", "orphan-stem", "extra-chunk")] string failure)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        Directory.CreateDirectory(scratch.Path);
        using RecordingColumnsDb db = new() { TrackCodeRebuild = true };
        PbtRocksDbPersistence target = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        using CancellationTokenSource cancellation = new();
        List<RebuildEntry> leaves = CodeAccountLeaves([.. Enumerable.Range(0, 4).Select(RebuildTestCode)]);
        ValueHash256 firstHash = ValueKeccak.Compute(RebuildTestCode(0));
        PbtVariableTreeKey firstChunk = (PbtVariableTreeKey)Eip8297KeyDerivation.OverflowCodeKey(firstHash.Bytes, 0);
        int firstChunkIndex = leaves.FindIndex(entry => entry.Key.Equals(firstChunk));
        switch (failure)
        {
            case "spool-cancel": cancellation.Cancel(); break;
            case "write-cancel": db.BeforeCodeWrite = cancellation.Cancel; break;
            case "invalid-leaf": leaves[firstChunkIndex] = new(firstChunk, Keccak.OfAnEmptyString.ValueHash256); break;
            case "zero-leaf": leaves[firstChunkIndex] = new(firstChunk, default); break;
            case "orphan-stem":
                ValueHash256 orphanHash = ValueKeccak.Compute(RebuildTestCode(99));
                foreach ((PbtPath key, ValueHash256 value) in Eip8297KeyDerivation.CodeLeaves(orphanHash, RebuildTestCode(99)))
                    leaves.Add(new((PbtVariableTreeKey)key, value));
                break;
            case "extra-chunk":
                leaves.Add(new((PbtVariableTreeKey)Eip8297KeyDerivation.OverflowCodeKey(firstHash.Bytes, 1), Keccak.OfAnEmptyString.ValueHash256));
                break;
        }

        Action reassemble = () => StageAndReassemble(target, scratch.Path, leaves, 3, cancellation.Token);
        if (failure.EndsWith("cancel", StringComparison.Ordinal)) Assert.That(reassemble, Throws.InstanceOf<OperationCanceledException>());
        else Assert.That(reassemble, Throws.InstanceOf<InvalidDataException>());

        AssertCodeRebuildReleased(db, target);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.AccountsVisited, Is.Zero);
            Assert.That(db.CodeReads, Is.Zero);
            Assert.That(Directory.GetFileSystemEntries(scratch.Path), Is.Empty);
        }
    }

    [Test]
    public void Code_staging_excludes_codeless_and_delegation_accounts([Values] bool delegation)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        Directory.CreateDirectory(scratch.Path);
        using RecordingColumnsDb db = new() { TrackCodeRebuild = true };
        PbtRocksDbPersistence target = new(db, new PbtConfig(), NullTrieNodeLog.Instance);
        byte[]? code = delegation ? Bytes.FromHexString("ef01000000000000000000000000000000000000000001") : null;
        List<RebuildEntry> leaves = [];
        PbtTestLeaves.AddAccount(leaves, TestItem.AddressA, new Account(1, 0), code);

        StageAndReassemble(target, scratch.Path, leaves, 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.AccountsVisited, Is.Zero);
            Assert.That(db.CodeReads, Is.Zero);
            Assert.That(db.CodeWrites, Is.EqualTo(delegation ? 1 : 0), "only staging writes the delegation code");
            Assert.That(db.CodeBatchSizes, Is.EqualTo(delegation ? new[] { 1 } : []));
        }
        AssertCodeRebuildReleased(db, target);
        if (delegation)
        {
            using IPbtPersistence.IReader reader = target.CreateReader();
            Assert.That(reader.GetCode(ValueKeccak.Compute(code!))!.Code.ToArray(), Is.EqualTo(code));
        }
    }

    [Test]
    public async Task Preimage_flat_import_reassembles_codes_without_code_leaf_reads([Values(1, 3)] int importConcurrency)
    {
        PbtConfig config = new()
        {
            ImportFromPreimageFlat = true,
            ImportConcurrency = importConcurrency,
            ExportConcurrency = importConcurrency == 1 ? 3 : 1,
            ImportSortBufferBytes = SortBufferBytes,
            ImportWindowSize = 1
        };
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        using MemDb codes = new();
        Dictionary<string, byte[]> model = [];
        PreimageRocksdbPersistence source = CreateSource(flatDb, batch =>
        {
            for (int index = 0; index < 4; index++)
            {
                byte[] code = RebuildTestCode(index);
                SetAccount(batch, model, Address.FromNumber((UInt256)(index + 1)), new Account(1, 0), code);
                codes[ValueKeccak.Compute(code).Bytes] = code;
            }
        });
        using RecordingColumnsDb db = new() { TrackCodeRebuild = true };
        PbtRocksDbPersistence target = new(db, config, NullTrieNodeLog.Instance);

        await CreateStep(source, codes, db, target, config).Execute(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.CodeWrites, Is.EqualTo(4));
            Assert.That(db.ActiveSnapshots, Is.Zero);
            Assert.That(db.ActiveCodeBatches, Is.Zero);
            Assert.That(db.DisposedResourceUses, Is.Zero);
            Assert.That(target.IsValid, Is.True);
        }
        using IPbtPersistence.IReader reader = target.CreateReader();
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    private static void AssertCodeRebuildReleased(RecordingColumnsDb db, PbtRocksDbPersistence target)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.ActiveSnapshots, Is.Zero);
            Assert.That(db.ActiveCodeWrites, Is.Zero);
            Assert.That(db.ActiveCodeBatches, Is.Zero);
            Assert.That(db.DisposedResourceUses, Is.Zero);
            Assert.That(target.IsValid, Is.False, "code reconstruction alone must not publish staging");
        }
    }

    /// <summary>The leaves of one contract account per code, with its code-chunk leaves.</summary>
    private static List<RebuildEntry> CodeAccountLeaves(List<byte[]> codes)
    {
        List<RebuildEntry> leaves = [];
        for (int index = 0; index < codes.Count; index++)
            PbtTestLeaves.AddAccount(leaves, Address.FromNumber((UInt256)(index + 1)),
                new Account(1, 0).WithChangedCodeHash(Keccak.Compute(codes[index])), codes[index]);
        return [.. leaves.DistinctBy(entry => entry.Key)];
    }

    private static void StageAndReassemble(PbtRocksDbPersistence target, string directory, List<RebuildEntry> leaves, int concurrency,
        CancellationToken cancellationToken)
    {
        leaves.Sort(static (left, right) => left.Key.Bytes.SequenceCompareTo(right.Key.Bytes));
        using PbtCodeReassembly codes = new(target, directory, SortBufferBytes, concurrency, LimboLogs.Instance, cancellationToken);
        using (PbtLeafIngestion.LogicalBatch batch = new(target, 1, cancellationToken))
        {
            PbtLeafIngestion.Stage(batch, leaves.GetEnumerator(), codes, cancellationToken);
            batch.Commit();
        }
        codes.Complete();
    }

    private static byte[] RebuildTestCode(int index)
    {
        byte[] code = new byte[5 + index % 3];
        code[0] = 0x63;
        BinaryPrimitives.WriteInt32BigEndian(code.AsSpan(1), index);
        return code;
    }

    /// <summary>A sort budget small enough to spill the leaves of every test across several runs.</summary>
    private const int SortBufferBytes = 1024;

    /// <summary>Header root that import must use as the resulting state's key.</summary>
    /// <remarks>It is unrelated to fixture tree roots to prevent accidental matches.</remarks>
    private static readonly Hash256 SourceStateRoot = TestItem.KeccakA;

    // Zero workers and window size use the built-in defaults; small values split leaves across parallel
    // partitions and fold windows that must produce the same root.
    [TestCase(5, 0, 0)]
    [TestCase(8000, 1, 1)]
    [TestCase(8000, 3, 3)]
    [TestCase(8000, 3, 0)]
    public async Task Imports_preimage_flat_state_into_pbt(int codeLength, int workers, int windowSize)
    {
        PbtConfig config = new() { ExportConcurrency = workers, ImportSortBufferBytes = SortBufferBytes, ImportWindowSize = windowSize };

        byte[] bigCode = new byte[codeLength];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);
        byte[] delegation = Bytes.FromHexString("ef01000000000000000000000000000000000000000001");
        Hash256 delegationHash = Keccak.Compute(delegation);

        Dictionary<string, byte[]> model = [];
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = CreateSource(flatDb, batch =>
        {
            SetAccount(batch, model, TestItem.AddressA, new Account(1, 100), null);
            // A non-empty flat storage root triggers storage fan-out; PBT omits it.
            SetAccount(batch, model, TestItem.AddressB, new Account(3, 42).WithChangedStorageRoot(TestItem.KeccakA), bigCode);
            SetSlot(batch, model, TestItem.AddressB, 5, 0xAB);      // header-region slot
            SetSlot(batch, model, TestItem.AddressB, 70, 0x07);     // storage-zone slot
            SetSlot(batch, model, TestItem.AddressB, 1000, 0x1234);
            // A second contract with the same code exercises content-addressed chunk deduplication.
            SetAccount(batch, model, TestItem.AddressC, new Account(9, 5).WithChangedStorageRoot(TestItem.KeccakB), bigCode);
            SetSlot(batch, model, TestItem.AddressC, 2000, 0x55);
            SetAccount(batch, model, TestItem.AddressD, new Account(1, 0), delegation);
            SetAccount(batch, model, TestItem.AddressE, new Account(1, 0), delegation);
        });

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;
        codeDb[delegationHash.Bytes] = delegation;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig(), NullTrieNodeLog.Instance);
        await CreateStep(flatSource, codeDb, pbtDb, pbtTarget, config).Execute(CancellationToken.None);

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)), "the state is keyed by the source's header root");
        Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)), "with the folded tree's own root recorded beside it");
        PbtScanReport scan = await new PbtScanner(pbtDb, config, LimboLogs.Instance).Scan(CancellationToken.None);
        Assert.That(scan.Accounts.RecordCount, Is.EqualTo(5), scan.Format());
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressB)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(PbtTestLeaves.ReadAccount(reader, TestItem.AddressC)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(reader.GetCode(bigCodeHash.ValueHash256)!.Code.ToArray(), Is.EqualTo(bigCode));
        Assert.That(PbtTestLeaves.ReadSlot(reader, TestItem.AddressB, 1000), Is.EqualTo((UInt256)0x1234));
        Assert.That(PbtTestLeaves.ReadSlot(reader, TestItem.AddressC, 2000), Is.EqualTo((UInt256)0x55));

        PbtRocksDbPersistence reopened = new(pbtDb, config, NullTrieNodeLog.Instance);
        PbtResourcePool pool = new(config);
        using PbtSnapshotBundle bundle = PbtSnapshotBundleTestExtensions.CreateBundle(pool, reopened.CreateReader());
        Account retained = bundle.GetAccount(TestItem.AddressB)!.WithChangedNonce(4).WithChangedBalance(43);
        bundle.SetAccount(TestItem.AddressB, retained);
        Assert.Throws<InvalidOperationException>(() => bundle.SetAccount(TestItem.AddressC, null), "imported code chunks are shared without a reference count");
        bundle.SetAccount(TestItem.AddressE, new Account(2, 0));
        ValueHash256 remainingRoot = bundle.Fold(reader.CurrentRoot);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 4, 43, bigCode);
        PbtReferenceModel.SetAccount(model, TestItem.AddressE, 2, 0);
        Assert.That(remainingRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
    }

    /// <summary>
    /// A retry after a pre-publication crash must clear staged new-format rows without reading stale nodes.
    /// </summary>
    [Test]
    public async Task Import_mode_recovers_an_interrupted_epoch_17_attempt()
    {
        PbtConfig config = new() { ImportFromPreimageFlat = true, ImportSortBufferBytes = SortBufferBytes };

        Dictionary<string, byte[]> model = [];
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = CreateSource(flatDb, batch =>
        {
            SetAccount(batch, model, TestItem.AddressA, new Account(1, 100), null);
            SetAccount(batch, model, TestItem.AddressB, new Account(3, 42).WithChangedStorageRoot(TestItem.KeccakA), null);
            SetSlot(batch, model, TestItem.AddressB, 5, 0xAB);
            SetSlot(batch, model, TestItem.AddressB, 1000, 0x1234);
        });

        using RecordingColumnsDb pbtDb = new();
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig(), NullTrieNodeLog.Instance);

        async Task<ValueHash256> Import()
        {
            await CreateStep(flatSource, new MemDb(), pbtDb, pbtTarget, config).Execute(CancellationToken.None);

            using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
            return reader.CurrentRoot;
        }

        using (IPbtPersistence.IWriteBatch staging = pbtTarget.CreateStagingWriteBatch())
        {
            staging.SetAccount(Eip8297KeyDerivation.AddressHash(TestItem.AddressC.ToHash().Bytes), new Account(1, 2).ToPbtAccount());
            PbtVariableTreeKey staleNodeKey = new([0x80]);
            PbtNodePath groupKey = new([], 0);
            using PbtNodeGroupStore staleNodes = new();
            staleNodes.SetNode(groupKey, PbtTreeHarness.EncodeLeaf(staleNodeKey));
            byte[]? payload = staleNodes.GetPhysicalNodeGroup(groupKey);
            staging.SetNodeGroup(groupKey, payload!);
            staging.Commit();
        }
        byte[] maximumLengthKey = new byte[PbtVariableTreeKey.MaxLength];
        maximumLengthKey.AsSpan().Fill(0xFF);
        pbtDb.GetColumnDb(PbtColumns.Storages)[maximumLengthKey] = SlotRunTestExtensions.SingleSlotRow(TestItem.KeccakA.Bytes);

        byte[] maximumGroupKey = new PbtStorageNodePath(Bytes.FromHexString(new string('f', 130) + "fc"), PbtTwoLevelGroupGeometry.MaxGroupDepth)
            .ToStorageKey(PbtColumns.StorageNodeGroups);
        PbtColumns[] groupColumns = [PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups];
        foreach (PbtColumns column in groupColumns)
            pbtDb.GetColumnDb(column)[maximumGroupKey] = Bytes.FromHexString("0x7f");
        IDb metadata = pbtDb.GetColumnDb(PbtColumns.Metadata);
        byte[]? staleRoot = metadata.Get("rootNodeGroup"u8);
        Assert.That(staleRoot, Is.Not.Null);
        // The fold may already be writing the new tree here, so only the stale rows are checked.
        pbtDb.BeforeFirstBatch = () =>
        {
            using (Assert.EnterMultipleScope())
            {
                Assert.That(metadata.Get("rootNodeGroup"u8), Is.Not.EqualTo(staleRoot), "the stale root must be removed before folding");
                foreach (PbtColumns column in groupColumns)
                    Assert.That(pbtDb.GetColumnDb(column).Get(maximumGroupKey), Is.Null, column.ToString());
            }
        };

        ValueHash256 rebuilt = await Import();
        Assert.That(rebuilt, Is.EqualTo(PbtReferenceModel.Root(model)), "a restart over an interrupted import must reproduce the source root");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.Get("validState"u8), Is.EqualTo(new byte[] { 1 }));
            Assert.That(metadata.Get("rootNodeGroup"u8), Is.Not.Null);
            foreach (PbtColumns column in groupColumns)
                Assert.That(pbtDb.GetColumnDb(column).Get(maximumGroupKey), Is.Null, column.ToString());
            Assert.That(pbtDb.GetColumnDb(PbtColumns.Storages).Get(maximumLengthKey), Is.Null, "the full keyspace must be cleared during retry");
        }
    }

    [Test]
    public async Task Scanner_counts_stored_records_without_integrity_checks([Values(0, 1, 2)] int concurrency)
    {
        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ScanTreeConcurrency = concurrency };
        PbtRocksDbPersistence persistence = new(db, config, NullTrieNodeLog.Instance);
        using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), TestItem.KeccakC.ValueHash256)) batch.Commit();
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
                storage[32] = zone;
                if (zone == Eip8297KeyDerivation.AccountZone) storage[^1] = PbtKeyDerivation.HeaderStorageOffset;
                Add(PbtColumns.Storages, storage, SlotRunTestExtensions.SingleSlotRow(TestItem.KeccakA.Bytes));
            }
        }
        Add(PbtColumns.Codes, TestItem.KeccakB.Bytes.ToArray(), Bytes.FromHexString("0x01"));
        Add(PbtColumns.Accounts, TestItem.KeccakC.Bytes.ToArray(), Nethermind.Serialization.Rlp.Rlp.Encode(new Account(1, 100).WithChangedCodeHash(TestItem.KeccakA)).Bytes);
        long[] expectedGroups = new long[PbtTwoLevelGroupGeometry.MaxPathDepth + 1];
        long[] expectedNodes = new long[expectedGroups.Length];
        long[] expectedPayloads = new long[expectedGroups.Length];
        Dictionary<PbtColumns, (long[] Groups, long[] Payloads, long[] Nodes)> expectedByPartition = [];
        long encodingBytes = 0;
        long expectedLeaves = 0;
        // A top group's bytes are counted under its physical column and its shape under its partition.
        (int Depth, byte Prefix, PbtColumns Column, PbtColumns Partition)[] groups =
        [
            (0, 0, PbtColumns.Metadata, PbtColumns.Metadata),
            (2, 0, PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups),
            (2, 0xC0, PbtColumns.TopNodeGroups, PbtColumns.StorageNodeGroups),
            (6, 0, PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups),
            (6, 0xFC, PbtColumns.TopNodeGroups, PbtColumns.StorageNodeGroups),
            (8, 0, PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups),
            (8, 1, PbtColumns.TopNodeGroups, PbtColumns.CodeNodeGroups),
            (8, 0xFF, PbtColumns.TopNodeGroups, PbtColumns.StorageNodeGroups),
            (PbtNodeGroupLayout.AccountTopDepth, 0x80, PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups),
            (PbtNodeGroupLayout.AccountTopDepth + PbtTwoLevelGroupGeometry.LevelsPerGroup, 0x80, PbtColumns.AccountNodeGroups, PbtColumns.AccountNodeGroups),
            (PbtNodeGroupLayout.StemTopDepth, 1, PbtColumns.TopNodeGroups, PbtColumns.CodeNodeGroups),
            (PbtNodeGroupLayout.StemTopDepth + PbtTwoLevelGroupGeometry.LevelsPerGroup, 1, PbtColumns.CodeNodeGroups, PbtColumns.CodeNodeGroups),
            (PbtNodeGroupLayout.StemTopDepth, 0xFF, PbtColumns.TopNodeGroups, PbtColumns.StorageNodeGroups),
            (PbtTwoLevelGroupGeometry.MaxGroupDepth, 0xFF, PbtColumns.StorageNodeGroups, PbtColumns.StorageNodeGroups),
        ];
        foreach ((int depth, byte prefix, PbtColumns column, PbtColumns partition) in groups)
        {
            byte[] pathBytes = new byte[(depth + 7) / 8];
            Array.Fill(pathBytes, byte.MaxValue);
            if (pathBytes.Length != 0) pathBytes[0] = prefix;
            if (depth % 8 != 0) pathBytes[^1] &= (byte)(0xFF << (8 - depth % 8));
            PbtStorageNodePath group = PbtStorageNodePath.Create(pathBytes, depth);
            PbtStorageNodePath node = depth == 0 ? group : PbtTestPaths.PathOf(group, 0);
            // Below the root every group stores one branch over two inline leaves, except where no longer key fits.
            bool inlineLeaves = depth != 0 && node.BitDepth < PbtTwoLevelGroupGeometry.MaxPathDepth;
            byte[] leftKey = new byte[inlineLeaves ? PbtVariableTreeKey.MaxLength : 0];
            byte[] rightKey = (byte[])leftKey.Clone();
            if (inlineLeaves)
            {
                PbtNodePathOperations.CopyTo(node, leftKey);
                PbtNodePathOperations.CopyTo(node, rightKey);
                rightKey[node.BitDepth / 8] |= (byte)(0x80 >> (node.BitDepth % 8));
                expectedLeaves += 2;
            }
            int keyOffset = inlineLeaves ? PbtNodeCodec.InlineKeyOffset(node.BitDepth) : 0;
            byte[] encoding = PbtTreeHarness.EncodeBranch([], 0, TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256, leftKey[keyOffset..], rightKey[keyOffset..]);
            byte[] payload = PbtNodeGroupEncoder.Encode(group, [new PbtNodeRecord(node, encoding)], default);
            Add(column, group.ToStorageKey(column), payload);
            expectedGroups[depth]++;
            expectedPayloads[depth] += payload.Length;
            expectedNodes[node.BitDepth]++;
            encodingBytes += encoding.Length;
            if (!expectedByPartition.TryGetValue(partition, out (long[] Groups, long[] Payloads, long[] Nodes) byColumn))
                expectedByPartition[partition] = byColumn = (new long[expectedGroups.Length], new long[expectedGroups.Length], new long[expectedGroups.Length]);
            byColumn.Groups[depth]++;
            byColumn.Payloads[depth] += payload.Length;
            byColumn.Nodes[node.BitDepth]++;
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
            Assert.That(report.NodeGroups.LeafCount, Is.EqualTo(expectedLeaves));
            Assert.That(report.NodeGroups.BranchCount, Is.EqualTo(groups.Length));
            Assert.That(report.NodeGroups.GroupsByOccupancy[1], Is.EqualTo(groups.Length));
            Assert.That(report.NodeGroups.NodeEncodingBytes, Is.EqualTo(encodingBytes));
            Assert.That(report.NodeGroups.RecordCount, Is.EqualTo(groups.Length));
            long groupKeyBytes = 0, groupValueBytes = 0;
            foreach (PbtColumns column in new[] { PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups, PbtColumns.Metadata })
            {
                groupKeyBytes += expected[column].Keys;
                groupValueBytes += expected[column].Values;
            }
            Assert.That(report.NodeGroups.KeyBytes, Is.EqualTo(groupKeyBytes));
            Assert.That(report.NodeGroups.ValueBytes, Is.EqualTo(groupValueBytes));
            foreach ((PbtColumns column, PbtScanReport.NodeGroupStats stats) in new[]
            {
                (PbtColumns.AccountNodeGroups, report.AccountNodeGroups),
                (PbtColumns.CodeNodeGroups, report.CodeNodeGroups),
                (PbtColumns.StorageNodeGroups, report.StorageNodeGroups),
                (PbtColumns.Metadata, report.MetadataRoot),
            })
            {
                (long[] groupsByDepth, long[] payloadsByDepth, long[] nodesByDepth) = expectedByPartition[column];
                Assert.That(stats.GroupsByDepth, Is.EqualTo(groupsByDepth), column.ToString());
                Assert.That(stats.PayloadBytesByDepth, Is.EqualTo(payloadsByDepth), column.ToString());
                Assert.That(stats.NodesByDepth, Is.EqualTo(nodesByDepth), column.ToString());
                Assert.That(stats.NodeCount, Is.EqualTo(groupsByDepth.Sum()), column.ToString());
            }
            Assert.That(report.TopNodeGroups.NodeCount, Is.Zero, "top group shapes are counted under their partitions");
            Assert.That(formatted, Does.Contain("not hash or reachability verification"));
            foreach (string label in new[] { "all partitions", nameof(PbtColumns.AccountNodeGroups), nameof(PbtColumns.CodeNodeGroups), nameof(PbtColumns.StorageNodeGroups) })
            {
                Assert.That(formatted, Does.Contain($"Node groups and contained nodes by bit depth ({label})"));
                Assert.That(formatted, Does.Contain($"Node-group occupancy ({label})"));
                Assert.That(formatted, Does.Contain($"Descendant sizes ({label})"));
            }
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
        logs.GetClassLogger<ProgressLogger>().Returns(scanLogger);
        logger.When(log => log.Info(Arg.Is<string>(message => message.Contains("PBT scan Accounts "))))
            .Do(_ => progressLogged.Set());
        if (periodic)
            db.ViewOpened = (column, _, _) =>
            {
                if (column == PbtColumns.Accounts && !progressLogged.Wait(TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("No periodic scan progress was logged.");
            };
        PbtScanReport report = await new PbtScanner(db, new PbtConfig { ScanTreeConcurrency = 2 }, logs).Scan(CancellationToken.None);
        foreach (PbtColumns column in new[] { PbtColumns.Accounts, PbtColumns.Storages, PbtColumns.Codes, PbtColumns.TopNodeGroups, PbtColumns.AccountNodeGroups, PbtColumns.CodeNodeGroups, PbtColumns.StorageNodeGroups })
            logger.Received().Info(Arg.Is<string>(message => message.Contains($"PBT scan {column} ")));
        Assert.That(report.Accounts.RecordCount, Is.EqualTo(1), "flat rows are counted without RLP decoding");
        if (periodic) Assert.That(progressLogged.IsSet, Is.True);
        Assert.That(db.ActiveViews, Is.Zero);
    }

    [Test]
    public async Task Scanner_startup_outcomes([Values("empty", "complete", "cancel", "malformed-root", "malformed-top", "malformed-account", "malformed-code", "malformed-storage")] string outcome)
    {
        using RecordingColumnsDb db = new();
        PbtConfig config = new() { ScanTreeConcurrency = 2 };
        PbtRocksDbPersistence persistence = new(db, config, NullTrieNodeLog.Instance);
        if (outcome != "empty")
            using (IPbtPersistence.IWriteBatch batch = persistence.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), TestItem.KeccakB.ValueHash256)) batch.Commit();
        bool malformed = outcome.StartsWith("malformed", StringComparison.Ordinal);
        if (malformed)
        {
            PbtColumns column = outcome switch
            {
                "malformed-root" => PbtColumns.Metadata,
                "malformed-top" => PbtColumns.TopNodeGroups,
                "malformed-account" => PbtColumns.AccountNodeGroups,
                "malformed-code" => PbtColumns.CodeNodeGroups,
                _ => PbtColumns.StorageNodeGroups,
            };
            byte prefix = column == PbtColumns.CodeNodeGroups ? (byte)1 : column == PbtColumns.StorageNodeGroups ? (byte)0xFF : (byte)0;
            byte[] key = new PbtNodePath([prefix], 8).ToStorageKey(column);
            db.GetColumnDb(column).Set(key, Bytes.FromHexString("0x7f"));
        }
        db.Recording = true;
        db.RecordAllColumns = true;
        using CancellationTokenSource cancellation = new();
        if (outcome == "cancel") cancellation.Cancel();
        ScanPbtTree step = new(new PbtScanner(db, config, LimboLogs.Instance), persistence, LimboLogs.Instance);
        if (malformed) Assert.That(async () => await step.Execute(cancellation.Token), Throws.InstanceOf<InvalidDataException>());
        else if (outcome == "cancel") Assert.That(async () => await step.Execute(cancellation.Token), Throws.InstanceOf<OperationCanceledException>());
        else await step.Execute(cancellation.Token);
        Assert.That(db.ActiveViews, Is.Zero);
    }

    [Test]
    public async Task Importer_bypasses_cached_persistence_wrapper()
    {
        PbtConfig config = new() { ImportSortBufferBytes = SortBufferBytes };
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = CreateSource(flatDb, batch => batch.SetAccount(TestItem.AddressA, new Account(1, 100)));

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence rawPersistence = new(pbtDb, config, NullTrieNodeLog.Instance);
        IPbtPersistence cachedWrapper = NSubstitute.Substitute.For<IPbtPersistence>();

        ContainerBuilder builder = new();
        builder
            .AddSingleton<IPersistence>(flatSource)
            .AddKeyedSingleton<IDb>(DbNames.Code, new MemDb())
            .AddSingleton<IColumnsDb<PbtColumns>>(pbtDb)
            .AddSingleton<PbtRocksDbPersistence>(rawPersistence)
            .AddSingleton<IPbtPersistence>(rawPersistence)
            .AddDecorator<IPbtPersistence>((_, _) => cachedWrapper)
            .AddSingleton<IDbFactory>(ScratchFactory())
            .AddSingleton<IPbtConfig>(config)
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<PbtRebuilder, PbtRocksDbPersistence, IPbtConfig, ILogManager>(
                (target, pbtConfig, logManager) => new PbtRebuilder(target, pbtConfig, pbtConfig.ImportConcurrency, logManager))
            .AddSingleton<PbtLeafIngestion>();
        builder.RegisterType<ImportPbtFromPreimageFlat>().WithAttributeFiltering();
        using IContainer container = builder.Build();

        ImportPbtFromPreimageFlat step = container.Resolve<ImportPbtFromPreimageFlat>();
        Assert.That(container.Resolve<IPbtPersistence>(), Is.SameAs(cachedWrapper));

        await step.Execute(CancellationToken.None);

        using IPbtPersistence.IReader reader = rawPersistence.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
    }

    [Test]
    public async Task Skips_when_pbt_already_populated()
    {
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = CreateSource(flatDb, batch => batch.SetAccount(TestItem.AddressA, new Account(1, 100)));

        // Ensure the persisted target state is not pre-genesis.
        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb, new PbtConfig(), NullTrieNodeLog.Instance);
        ValueHash256 existingRoot = new(Keccak.Compute("existing").Bytes);
        using (IPbtPersistence.IWriteBatch persisted = pbtTarget.CreateWriteBatch(StateId.PreGenesis, new StateId(1, existingRoot), default))
            persisted.Commit();

        await CreateStep(flatSource, new MemDb(), pbtDb, pbtTarget, new PbtConfig()).Execute(CancellationToken.None);

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(1, existingRoot)), "the existing state is left untouched");
    }

    [TestCase("missing-code")]
    [TestCase("persistence")]
    [TestCase("cancellation")]
    [TestCase("code-spool-cancellation")]
    [TestCase("code-write-cancellation")]
    [TestCase("code-validation")]
    public async Task Failed_import_publishes_nothing_and_retries(string failure)
    {
        using TempPath scratch = TempPath.GetTempDirectory();
        Directory.CreateDirectory(scratch.Path);
        using SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        byte[] code = Bytes.FromHexString("0x6001600055");
        Hash256 codeHash = Keccak.Compute(code);
        Dictionary<string, byte[]> model = [];
        PreimageRocksdbPersistence source = CreateSource(flatDb, batch =>
        {
            SetAccount(batch, model, TestItem.AddressA, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA), code);
            for (uint index = 0; index < 100; index++)
                SetSlot(batch, model, TestItem.AddressA, PbtKeyDerivation.HeaderStorageOffset + index * SlotRun.Width, 1);
        });
        using MemDb codes = new();
        if (failure != "missing-code") codes[codeHash.Bytes] = code;
        // Recording from the start makes the fold's first window, not the final publication, the first group commit.
        using RecordingColumnsDb db = new() { Recording = true };
        PbtConfig config = new() { ExportConcurrency = 1, ImportSortBufferBytes = SortBufferBytes, ImportWindowSize = 1, ImportFromPreimageFlat = true, ImportConcurrency = 3 };
        PbtRocksDbPersistence target = new(db, config, NullTrieNodeLog.Instance);
        using CancellationTokenSource cancellation = new();
        db.TrackCodeRebuild = failure.StartsWith("code-", StringComparison.Ordinal);
        db.BeforeAccountWrite = () => { if (failure == "code-spool-cancellation") cancellation.Cancel(); };
        db.BeforeCodeWrite = () =>
        {
            if (failure == "code-write-cancellation") cancellation.Cancel();
            if (failure == "code-validation") throw new InvalidDataException("injected code reconstruction failure");
        };
        db.AfterGroupCommit = () =>
        {
            if (db.GroupCommits != 1) return;
            if (failure == "persistence") throw new IOException("injected fold persistence failure");
            if (failure == "cancellation") cancellation.Cancel();
        };

        Task import = CreateStep(source, codes, db, target, config, scratch.Path).Execute(cancellation.Token);

        if (failure.EndsWith("cancellation", StringComparison.Ordinal)) Assert.That(async () => await import.WaitAsync(TimeSpan.FromSeconds(30)), Throws.InstanceOf<OperationCanceledException>());
        else
        {
            Exception? error = await Assert.CatchAsync(async () => await import.WaitAsync(TimeSpan.FromSeconds(30)));
            Assert.That(error, failure switch
            {
                "missing-code" => Is.TypeOf<InvalidDataException>().With.Message.Contains("Missing source code"),
                "code-validation" => Is.TypeOf<InvalidDataException>().With.Message.Contains("injected code reconstruction failure"),
                _ => Is.TypeOf<IOException>().With.Message.Contains("injected fold persistence failure")
            });
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("validState"u8), Is.Null);
            Assert.That(db.GetColumnDb(PbtColumns.Metadata).Get("currentState"u8), Is.Null);
            Assert.That(db.ActiveSnapshots, Is.Zero);
            Assert.That(db.ActiveCodeBatches, Is.Zero);
            Assert.That(db.DisposedResourceUses, Is.Zero);
            Assert.That(Directory.GetFileSystemEntries(Path.Combine(scratch.Path, "import")), Is.Empty);
        }
        db.AfterGroupCommit = null;
        db.BeforeAccountWrite = null;
        db.BeforeCodeWrite = null;
        codes[codeHash.Bytes] = code;
        await CreateStep(source, codes, db, target, config, scratch.Path).Execute(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60));
        using IPbtPersistence.IReader reader = target.CreateReader();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.CurrentRoot, Is.EqualTo(PbtReferenceModel.Root(model)));
            Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, SourceStateRoot)));
            Assert.That(Directory.GetFileSystemEntries(Path.Combine(scratch.Path, "import")), Is.Empty);
        }
    }

    private static ImportPbtFromPreimageFlat CreateStep(IPersistence source, IDb codes, IColumnsDb<PbtColumns> db, PbtRocksDbPersistence target, PbtConfig config,
        string? scratchDirectory = null) =>
        new(source, codes, db,
            new PbtLeafIngestion(target, new PbtRebuilder(target, config, config.ImportConcurrency, LimboLogs.Instance), ScratchFactory(scratchDirectory), config, LimboLogs.Instance), target, ScratchFactory(scratchDirectory), config, LimboLogs.Instance);

    private static IDbFactory ScratchFactory(string? directory = null)
    {
        IDbFactory dbFactory = Substitute.For<IDbFactory>();
        dbFactory.GetFullDbPath(Arg.Any<DbSettings>()).Returns(directory ?? Path.GetTempPath());
        return dbFactory;
    }

    private static PreimageRocksdbPersistence CreateSource(SnapshotableMemColumnsDb<FlatDbColumns> flatDb, Action<IPersistence.IWriteBatch> write)
    {
        PreimageRocksdbPersistence source = new(flatDb, LimboLogs.Instance, FlatLayout.PreimageFlat, Flat.Persistence.TrieNodeLog.NullTrieNodeLog.Instance);
        using IPersistence.IWriteBatch batch = source.CreateWriteBatch(StateId.PreGenesis, new StateId(SourceBlock, SourceStateRoot), WriteFlags.None);
        write(batch);
        return source;
    }

    private sealed class RecordingColumnsDb : IColumnsDb<PbtColumns>
    {
        private readonly SnapshotableMemColumnsDb<PbtColumns> _database = new("pbt");
        private readonly Dictionary<PbtColumns, IDb> _columns = [];
        public readonly ConcurrentDictionary<string, int> Rows = new();
        public Action? BeforeFirstBatch;
        public Action? AfterGroupCommit;
        public Action<PbtColumns, byte[], byte[]>? ViewOpened;
        public bool Recording;
        public bool RecordAllColumns;
        public bool ForbidPointReads;
        public int ActiveViews;
        public int GroupCommits;
        public int CodeReads;
        public bool TrackCodeRebuild;
        public Action? BeforeCodeWrite;
        public Action? BeforeAccountWrite;
        public int AccountsVisited;
        public int CodeWrites;
        public int ActiveSnapshots;
        public int ActiveCodeWrites;
        public int MaxActiveCodeWrites;
        public int ActiveCodeBatches;
        public int DisposedResourceUses;
        public readonly ConcurrentQueue<int> CodeBatchSizes = new();
        private int _flushed;

        private static void RecordMaximum(ref int maximum, int active)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref maximum);
                if (active <= previous) return;
            } while (Interlocked.CompareExchange(ref maximum, active, previous) != previous);
        }

        public RecordingColumnsDb()
        {
            foreach (PbtColumns column in Enum.GetValues<PbtColumns>())
                _columns[column] = new RecordingDb(this, column, _database.GetColumnDb(column));
        }

        public IDb GetColumnDb(PbtColumns key) => _columns[key];
        public IEnumerable<PbtColumns> ColumnKeys => _database.ColumnKeys;
        public IColumnDbSnapshot<PbtColumns> CreateSnapshot()
        {
            Interlocked.Increment(ref ActiveSnapshots);
            return new RecordingSnapshot(this, _database.CreateSnapshot());
        }

        private sealed class RecordingSnapshot(RecordingColumnsDb owner, IColumnDbSnapshot<PbtColumns> snapshot) : IColumnDbSnapshot<PbtColumns>
        {
            private int _disposed;
            private int _reads;
            public IReadOnlyKeyValueStore GetColumn(PbtColumns key) => key switch
            {
                PbtColumns.Codes => new RecordingCodeStore(owner, this, snapshot.GetColumn(key)),
                PbtColumns.Accounts when owner.TrackCodeRebuild => new RecordingAccounts(owner, (ISortedKeyValueStore)snapshot.GetColumn(key)),
                _ => snapshot.GetColumn(key)
            };
            public void EnterRead()
            {
                Interlocked.Increment(ref _reads);
                if (Volatile.Read(ref _disposed) != 0)
                {
                    Interlocked.Decrement(ref _reads);
                    Interlocked.Increment(ref owner.DisposedResourceUses);
                    throw new ObjectDisposedException(nameof(RecordingSnapshot));
                }
            }
            public void ExitRead() => Interlocked.Decrement(ref _reads);
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (Volatile.Read(ref _reads) != 0) Interlocked.Increment(ref owner.DisposedResourceUses);
                snapshot.Dispose();
                Interlocked.Decrement(ref owner.ActiveSnapshots);
            }
        }

        private sealed class RecordingCodeStore(RecordingColumnsDb owner, RecordingSnapshot snapshot, IReadOnlyKeyValueStore store) : IReadOnlyKeyValueStore
        {
            public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
            {
                snapshot.EnterRead();
                Interlocked.Increment(ref owner.CodeReads);
                try
                {
                    return store.Get(key, flags);
                }
                finally
                {
                    snapshot.ExitRead();
                }
            }
        }

        private sealed class RecordingAccounts(RecordingColumnsDb owner, ISortedKeyValueStore store) : ISortedKeyValueStore
        {
            public byte[]? FirstKey => store.FirstKey;
            public byte[]? LastKey => store.LastKey;
            public byte[]? Get(scoped ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None) => store.Get(key, flags);
            public ISortedView GetViewBetween(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive, ReadFlags flags = ReadFlags.None) =>
                new AccountView(owner, store.GetViewBetween(firstKeyInclusive, lastKeyExclusive, flags));

            private sealed class AccountView(RecordingColumnsDb owner, ISortedView view) : ISortedView
            {
                public ReadOnlySpan<byte> CurrentKey => view.CurrentKey;
                public ReadOnlySpan<byte> CurrentValue => view.CurrentValue;
                public bool StartBefore(ReadOnlySpan<byte> value) => view.StartBefore(value);
                public bool MoveNext()
                {
                    if (!view.MoveNext()) return false;
                    Interlocked.Increment(ref owner.AccountsVisited);
                    return true;
                }
                public void Dispose() => view.Dispose();
            }
        }
        public IColumnsWriteBatch<PbtColumns> StartWriteBatch()
        {
            Interlocked.Exchange(ref BeforeFirstBatch, null)?.Invoke();
            IColumnsWriteBatch<PbtColumns> batch = _database.StartWriteBatch();
            bool trackCodeBatches = TrackCodeRebuild;
            if (trackCodeBatches) Interlocked.Increment(ref ActiveCodeBatches);
            return new RecordingBatch(this, batch, trackCodeBatches);
        }

        public void Dispose() => _database.Dispose();
        public void Flush(bool onlyWal = false)
        {
            _database.Flush(onlyWal);
            if (!onlyWal && Interlocked.Exchange(ref _flushed, 1) == 0) Recording = true;
        }

        private sealed class RecordingBatch(RecordingColumnsDb owner, IColumnsWriteBatch<PbtColumns> batch, bool trackCodeBatches) : IColumnsWriteBatch<PbtColumns>
        {
            private bool _groups;
            private int _codes;
            private int _codeWrites;
            private int _disposed;
            public IWriteBatch GetColumnBatch(PbtColumns key)
            {
                IWriteBatch columnBatch = batch.GetColumnBatch(key);
                if (key == PbtColumns.Codes && owner.TrackCodeRebuild)
                {
                    Interlocked.Exchange(ref _codes, 1);
                    return new RecordingCodeBatch(owner, this, columnBatch);
                }
                if (key == PbtColumns.Accounts && owner.BeforeAccountWrite is not null) return new RecordingAccountBatch(owner, columnBatch);
                if (key == PbtColumns.Metadata) return new RecordingMetadataBatch(owner, this, columnBatch);
                if (key is PbtColumns.TopNodeGroups or PbtColumns.AccountNodeGroups or PbtColumns.CodeNodeGroups or PbtColumns.StorageNodeGroups && owner.Recording) _groups = true;
                return columnBatch;
            }
            private sealed class RecordingCodeBatch(RecordingColumnsDb owner, RecordingBatch ownerBatch, IWriteBatch codes) : IWriteBatch
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    RecordMaximum(ref owner.MaxActiveCodeWrites, Interlocked.Increment(ref owner.ActiveCodeWrites));
                    try
                    {
                        if (Volatile.Read(ref ownerBatch._disposed) != 0)
                        {
                            Interlocked.Increment(ref owner.DisposedResourceUses);
                            throw new ObjectDisposedException(nameof(RecordingBatch));
                        }
                        owner.BeforeCodeWrite?.Invoke();
                        Interlocked.Increment(ref owner.CodeWrites);
                        Interlocked.Increment(ref ownerBatch._codeWrites);
                        codes.Set(key, value, flags);
                    }
                    finally { Interlocked.Decrement(ref owner.ActiveCodeWrites); }
                }
                public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => codes.Merge(key, value, flags);
                public void Clear() => codes.Clear();
                public void Dispose() => codes.Dispose();
            }

            private sealed class RecordingAccountBatch(RecordingColumnsDb owner, IWriteBatch accounts) : IWriteBatch
            {
                public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
                {
                    owner.BeforeAccountWrite?.Invoke();
                    accounts.Set(key, value, flags);
                }
                public void Merge(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value, WriteFlags flags = WriteFlags.None) => accounts.Merge(key, value, flags);
                public void Clear() => accounts.Clear();
                public void Dispose() => accounts.Dispose();
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
                Interlocked.Exchange(ref _codeWrites, 0);
                batch.Clear();
            }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_codes != 0) RecordMaximum(ref owner.MaxActiveCodeWrites, Interlocked.Increment(ref owner.ActiveCodeWrites));
                try
                {
                    batch.Dispose();
                    if (_codes != 0 && _codeWrites != 0) owner.CodeBatchSizes.Enqueue(_codeWrites);
                    if (_groups)
                    {
                        Interlocked.Increment(ref owner.GroupCommits);
                        owner.AfterGroupCommit?.Invoke();
                    }
                }
                finally
                {
                    if (_codes != 0) Interlocked.Decrement(ref owner.ActiveCodeWrites);
                    if (trackCodeBatches) Interlocked.Decrement(ref owner.ActiveCodeBatches);
                }
            }
        }

        private sealed class RecordingDb(RecordingColumnsDb owner, PbtColumns column, IDb database) : IDb, ISortedKeyValueStore, IRangeRemovableKeyValueStore
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
            public void RemoveRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) => ((IRangeRemovableKeyValueStore)database).RemoveRange(firstKeyInclusive, lastKeyExclusive);
            public void ReclaimRange(ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive) => ((IRangeRemovableKeyValueStore)database).ReclaimRange(firstKeyInclusive, lastKeyExclusive);
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
            public ReadOnlySpan<byte> CurrentKey => view.CurrentKey;
            public ReadOnlySpan<byte> CurrentValue => view.CurrentValue;
            public bool StartBefore(ReadOnlySpan<byte> value) => view.StartBefore(value);
            public bool MoveNext()
            {
                if (!view.MoveNext()) return false;
                owner.Rows.AddOrUpdate($"{column}:{Convert.ToHexString(view.CurrentKey)}", 1, static (_, count) => count + 1);
                return true;
            }
            public void Dispose()
            {
                view.Dispose();
                Interlocked.Decrement(ref owner.ActiveViews);
            }
        }
    }
}
