// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Pbt.Persistence;
using Nethermind.State.Pbt.Steps;
using NUnit.Framework;
using FlatStateId = Nethermind.State.Flat.StateId;

namespace Nethermind.State.Pbt.Test;

public class ImportPbtFromPreimageFlatTests
{
    private const ulong SourceBlock = 7;

    // 0 leaves the built-in window size, so the whole import folds as one window; the small values
    // force many windows, which the stem-ordered column scan must fold to the very same root
    [TestCase(0)]
    [TestCase(1)]
    [TestCase(3)]
    public async Task Imports_preimage_flat_state_into_pbt_and_exits(int windowSize)
    {
        PbtConfig config = new() { ImportWindowSize = windowSize };

        // > 128 chunks (3968 bytes) so the overflow-code zone is exercised end-to-end
        byte[] bigCode = new byte[5000];
        for (int i = 0; i < bigCode.Length; i += 10) bigCode[i] = 0x63;
        Hash256 bigCodeHash = Keccak.Compute(bigCode);

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42, bigCode);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);      // header-region slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 70, 0x07);     // storage-zone slot
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);
        // a second contract with the same code exercises the overflow-chunk dedup: its content-addressed
        // chunks are emitted once, and the root must still match the reference tree
        PbtReferenceModel.SetAccount(model, TestItem.AddressC, 9, 5, bigCode);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            // a contract with storage carries a non-empty storage root in a real flat db, which is what
            // the importer keys off to fan its storage out to the worker pool (the PBT tree omits it)
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedCodeHash(bigCodeHash).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 70, SlotValue.FromSpanWithoutLeadingZero([0x07]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
            batch.SetAccount(TestItem.AddressC, new Account(9, 5).WithChangedCodeHash(bigCodeHash));
        }

        MemDb codeDb = new();
        codeDb[bigCodeHash.Bytes] = bigCode;

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        RecordingExitSource exitSource = new();
        // the same columns db must back both the persistence and the step, or phase two scans an empty
        // database and the test passes while proving nothing
        ImportPbtFromPreimageFlat step = new(flatSource, flatDb, codeDb, pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, PbtReferenceModel.Root(model))));
        Assert.That(reader.GetAccount(TestItem.AddressA)!.Balance, Is.EqualTo((UInt256)100));
        Assert.That(reader.GetAccount(TestItem.AddressB)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(reader.GetAccount(TestItem.AddressC)!.CodeHash, Is.EqualTo((Hash256)bigCodeHash));
        Assert.That(EvmWordSlot.AsReadOnlySpan(reader.GetSlot(TestItem.AddressB, 1000)).ToArray(), Is.EqualTo(((UInt256)0x1234).ToBigEndian()));
    }

    /// <summary>
    /// An account whose storage is entirely header-region (every slot &lt; 64) contributes zone-0 rows
    /// and no zone-8 rows at all, so the merge-join must consume its slots without a storage-zone pass
    /// to fall back on. An account with neither storage nor code covers the opposite end.
    /// </summary>
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
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(2, 200).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 0, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(TestItem.AddressB, 63, SlotValue.FromSpanWithoutLeadingZero([0x22]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, flatDb, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, PbtReferenceModel.Root(model))));
    }

    /// <summary>
    /// A flat storage key splits the address around the slot, so accounts sharing its leading four
    /// bytes sit interleaved under them in slot order rather than one after another. Mined vanity
    /// addresses collide there in practice, so the copy has to return every slot to the account it came
    /// from instead of to whichever account opened the group.
    /// </summary>
    [Test]
    public async Task Imports_slots_of_accounts_sharing_a_storage_key_prefix()
    {
        PbtConfig config = new();

        // equal in the four address bytes a storage key leads with, so their slots interleave
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
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(first, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetAccount(second, new Account(2, 200).WithChangedStorageRoot(TestItem.KeccakB));
            batch.SetStorage(first, 1, SlotValue.FromSpanWithoutLeadingZero([0x11]));
            batch.SetStorage(first, 1000, SlotValue.FromSpanWithoutLeadingZero([0x22]));
            batch.SetStorage(second, 1, SlotValue.FromSpanWithoutLeadingZero([0x33]));
            batch.SetStorage(second, 1000, SlotValue.FromSpanWithoutLeadingZero([0x44]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, flatDb, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.EqualTo(0));
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(SourceBlock, PbtReferenceModel.Root(model))));
        Assert.That(EvmWordSlot.AsReadOnlySpan(reader.GetSlot(first, 1000)).ToArray(), Is.EqualTo(((UInt256)0x22).ToBigEndian()));
        Assert.That(EvmWordSlot.AsReadOnlySpan(reader.GetSlot(second, 1000)).ToArray(), Is.EqualTo(((UInt256)0x44).ToBigEndian()));
    }

    /// <summary>
    /// A crash after the data was written but before the persisted-state pointer advanced leaves the
    /// target holding a full flat copy plus whatever leaf blobs and trie nodes the fold got through.
    /// The next run must reproduce the same root over that debris rather than merging with it — the
    /// fold starts from an empty root, so every stem is structurally new and its stale blob is
    /// overwritten instead of read.
    /// </summary>
    [Test]
    public async Task Rerunning_after_an_interrupted_import_reproduces_the_root()
    {
        PbtConfig config = new();

        Dictionary<string, byte[]> model = [];
        PbtReferenceModel.SetAccount(model, TestItem.AddressA, 1, 100);
        PbtReferenceModel.SetAccount(model, TestItem.AddressB, 3, 42);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 5, 0xAB);
        PbtReferenceModel.SetSlot(model, TestItem.AddressB, 1000, 0x1234);

        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
            batch.SetAccount(TestItem.AddressB, new Account(3, 42).WithChangedStorageRoot(TestItem.KeccakA));
            batch.SetStorage(TestItem.AddressB, 5, SlotValue.FromSpanWithoutLeadingZero([0xAB]));
            batch.SetStorage(TestItem.AddressB, 1000, SlotValue.FromSpanWithoutLeadingZero(Bytes.FromHexString("0x1234")));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);

        async Task<ValueHash256> Import()
        {
            RecordingExitSource exitSource = new();
            ImportPbtFromPreimageFlat step = new(flatSource, flatDb, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, config), pbtTarget, config, exitSource, LimboLogs.Instance);
            await step.Execute(CancellationToken.None);
            Assert.That(exitSource.ExitCode, Is.EqualTo(0));

            using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
            return reader.CurrentState.StateRoot;
        }

        ValueHash256 first = await Import();
        Assert.That(first, Is.EqualTo(PbtReferenceModel.Root(model)));

        // rewind only the pointer, leaving the copied rows, blobs and nodes in place
        pbtDb.GetColumnDb(PbtColumns.Metadata).Remove("currentState"u8);

        Assert.That(await Import(), Is.EqualTo(first), "a restart over an interrupted import must reproduce the same root");
    }

    [Test]
    public async Task Consecutive_storage_slots_share_one_stem()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100).WithChangedStorageRoot(TestItem.KeccakA));
            // two storage-zone slots in the same 256-block: 100>>8 == 101>>8 == 0, so they share a stem
            batch.SetStorage(TestItem.AddressA, 100, SlotValue.FromSpanWithoutLeadingZero([0xAA]));
            batch.SetStorage(TestItem.AddressA, 101, SlotValue.FromSpanWithoutLeadingZero([0xBB]));
        }

        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        RecordingExitSource exitSource = new();
        // FlushEntryInterval 1 forces the two slots into separate windows, so a same-stem cross-window merge is exercised too
        ImportPbtFromPreimageFlat step = new(flatSource, flatDb, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()) { FlushEntryInterval = 1 }, pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);
        Assert.That(exitSource.ExitCode, Is.EqualTo(0));

        PbtScanReport report = await new PbtScanner(pbtDb, new PbtConfig(), LimboLogs.Instance).Scan(CancellationToken.None);
        Assert.That(report.StorageLeaves.BlobCount, Is.EqualTo(1), "two slots in one 256-block must share a single storage stem");
        Assert.That(report.StorageLeaves.LeafCount, Is.EqualTo(2), "the shared stem's blob must hold both slots' leaves");
    }

    [Test]
    public async Task Skips_when_pbt_already_populated()
    {
        SnapshotableMemColumnsDb<FlatDbColumns> flatDb = new("flat");
        PreimageRocksdbPersistence flatSource = new(flatDb, LimboLogs.Instance);
        using (IPersistence.IWriteBatch batch = flatSource.CreateWriteBatch(FlatStateId.PreGenesis, new FlatStateId(SourceBlock, Keccak.EmptyTreeHash), WriteFlags.None))
        {
            batch.SetAccount(TestItem.AddressA, new Account(1, 100));
        }

        // seed the target so its persisted state is no longer pre-genesis
        SnapshotableMemColumnsDb<PbtColumns> pbtDb = new("pbt");
        PbtRocksDbPersistence pbtTarget = new(pbtDb);
        ValueHash256 existingRoot = new(Keccak.Compute("existing").Bytes);
        using (pbtTarget.CreateWriteBatch(StateId.PreGenesis, new StateId(1, existingRoot), WriteFlags.None)) { }

        RecordingExitSource exitSource = new();
        ImportPbtFromPreimageFlat step = new(flatSource, flatDb, new MemDb(), pbtDb, new PbtRebuilder(pbtTarget, LimboLogs.Instance, new PbtConfig()), pbtTarget, new PbtConfig(), exitSource, LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(exitSource.ExitCode, Is.Null, "an already-populated target is skipped without exiting");
        using IPbtPersistence.IReader reader = pbtTarget.CreateReader();
        Assert.That(reader.CurrentState, Is.EqualTo(new StateId(1, existingRoot)), "the existing state is left untouched");
    }

    private sealed class RecordingExitSource : IProcessExitSource
    {
        public int? ExitCode { get; private set; }
        public CancellationToken Token => CancellationToken.None;
        public void Exit(int exitCode) => ExitCode ??= exitCode;
    }
}
