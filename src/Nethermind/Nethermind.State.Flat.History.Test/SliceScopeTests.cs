// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class SliceScopeTests
{
    private static readonly Address SlicedAddress = TestItem.AddressA;
    private static readonly Address NonSlicedAddress = TestItem.AddressB;
    private static readonly Address OtherSlicedAddress = TestItem.AddressC;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void ScopeKeyLength_StaysNarrowerThanAnAccountPath() =>
        Assert.That(HistoryKeyLayout.ScopeKeyLength, Is.LessThan(HistoryKeyLayout.AccountKeyLength),
            "a storage row carries only this much of its address, so widening the scope key to the whole account path would leave every storage row unable to resolve its own slice and the pruner would delete it");

    [Test]
    public void ResolveScope_GivenAWholeAccountPathInsteadOfAScopeKey_Refuses()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));

        Assert.That(() => availability.ResolveScope(new byte[HistoryKeyLayout.AccountKeyLength], knownGeneralFloor: 0), Throws.ArgumentException,
            "silently truncating here would let a scope be published under a key that ResolveScope then never matches, so the address would resolve to the general floor and its retained rows would be pruned - with no exception and no failing test");
    }

    [Test]
    public void PublishScope_GivenAWholeAccountPathInsteadOfAScopeKey_Refuses()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));

        Assert.That(() => availability.PublishScope(new byte[HistoryKeyLayout.AccountKeyLength], floor: 0), Throws.ArgumentException);
    }

    [Test]
    public void PublishScope_AlongsideGlobalFloor_LeavesGlobalFloorApiUnchanged()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        availability.PublishGlobalFloor(100);
        availability.PublishScope(ScopeKeyOf(SlicedAddress), floor: 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(availability.TryGetGlobalFloor(out ulong globalFloor), Is.True);
            Assert.That(globalFloor, Is.EqualTo(100UL));
            Assert.That(availability.GetScopes().Count, Is.EqualTo(1));
        }
    }

    [Test]
    public void ResolveScope_ForAPointScopeWithADeeperFloor_ReturnsTheScopeNotTheGeneralFallback()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        availability.PublishGlobalFloor(100);
        availability.PublishScope(ScopeKeyOf(SlicedAddress), floor: 0);

        ScopeFloor resolved = availability.ResolveScope(ScopeKeyOf(SlicedAddress));
        Assert.That(resolved.IsGeneral, Is.False);
        Assert.That(resolved.Floor, Is.EqualTo(0UL), "the point scope's own floor must win, not min(pointFloor, generalFloor)");
    }

    [Test]
    public void ResolveScope_ForAPointScopeWithAShallowerFloorThanGeneral_StillReturnsTheScopesOwnFloor()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        availability.PublishGlobalFloor(10);
        availability.PublishScope(ScopeKeyOf(SlicedAddress), floor: 500);

        ScopeFloor resolved = availability.ResolveScope(ScopeKeyOf(SlicedAddress));
        Assert.That(resolved.IsGeneral, Is.False);
        Assert.That(resolved.Floor, Is.EqualTo(500UL), "a point scope's own (here, shallower) floor must never be cheated down to the general floor's lower value");
    }

    [Test]
    public void ResolveScope_ForAnAddressWithNoPointScope_FallsBackToGeneralFloor()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        availability.PublishGlobalFloor(100);
        availability.PublishScope(ScopeKeyOf(SlicedAddress), floor: 0);

        ScopeFloor resolved = availability.ResolveScope(ScopeKeyOf(NonSlicedAddress));
        Assert.That(resolved.IsGeneral, Is.True);
        Assert.That(resolved.Floor, Is.EqualTo(100UL));
    }

    [Test]
    public void ResolveScope_AfterPublishOnTheSameInstance_ObservesTheNewScopeImmediately()
    {
        HistoryAvailability availability = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));
        Assert.That(availability.ResolveScope(ScopeKeyOf(SlicedAddress)).IsGeneral, Is.True, "precondition: no scope published yet");

        availability.PublishScope(ScopeKeyOf(SlicedAddress), floor: 0);

        Assert.That(availability.ResolveScope(ScopeKeyOf(SlicedAddress)).IsGeneral, Is.False, "a publish must invalidate the cache the earlier ResolveScope call primed");
    }

    [Test]
    public void RunOnePass_ForASlicedAddressAccountRow_RetainsItBelowTheGeneralFloor()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, SlicedAddress, block: 5, new Account(1, 100));
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, NonSlicedAddress, block: 5, new Account(2, 200));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        (_, HistoryWriter writer, HistoryWindowPruner pruner) = CreateWriterAndPruner(retentionBlocks: 8, sliceAddresses: SlicedAddress.ToString());
        pruner.ReconcileSliceScopes();
        Assert.That(writer.LastCapturedBlock, Is.EqualTo(20UL), "precondition: watermark set to 20");

        pruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];

        using (Assert.EnterMultipleScope())
        {
            int slicedWritten = accountHistoryV3.TryGetValueBeforeNextChange(0, AccountKeyOf(SlicedAddress), buffer, out ulong slicedFoundAt);
            Assert.That(slicedWritten, Is.GreaterThanOrEqualTo(0), "the sliced address's block-5 row must survive the general-window prune floor (12)");
            Assert.That(slicedFoundAt, Is.EqualTo(5UL));

            int nonSlicedWritten = accountHistoryV3.TryGetValueBeforeNextChange(0, AccountKeyOf(NonSlicedAddress), buffer, out _);
            Assert.That(nonSlicedWritten, Is.EqualTo(-1), "the non-sliced address's block-5 row is below the floor (12) and must be pruned exactly as it is today");
        }

        pruner.Dispose();
    }

    [Test]
    public void RunOnePass_ForASlicedAddressStorageRow_RetainsItBelowTheGeneralFloor()
    {
        UInt256 slot = 7;
        HistoryColumnsWriter.RecordStorageV3(_historyColumns, SlicedAddress, slot, block: 5, [0x11]);
        HistoryColumnsWriter.RecordStorageV3(_historyColumns, NonSlicedAddress, slot, block: 5, [0x22]);
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        (_, HistoryWriter writer, HistoryWindowPruner pruner) = CreateWriterAndPruner(retentionBlocks: 8, sliceAddresses: SlicedAddress.ToString());
        pruner.ReconcileSliceScopes();
        Assert.That(writer.LastCapturedBlock, Is.EqualTo(20UL), "precondition: watermark set to 20");

        pruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 storageHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.StorageHistory));
        Span<byte> buffer = stackalloc byte[256];

        using (Assert.EnterMultipleScope())
        {
            int slicedWritten = storageHistoryV3.TryGetValueBeforeNextChange(0, StorageKeyOf(SlicedAddress, slot), buffer, out ulong slicedFoundAt);
            Assert.That(slicedWritten, Is.GreaterThanOrEqualTo(0),
                "the sliced address's storage row must survive - a swapped 4B/16B split in ExtractAddressKey would resolve the wrong scope and silently lose this row");
            Assert.That(slicedFoundAt, Is.EqualTo(5UL));

            int nonSlicedWritten = storageHistoryV3.TryGetValueBeforeNextChange(0, StorageKeyOf(NonSlicedAddress, slot), buffer, out _);
            Assert.That(nonSlicedWritten, Is.EqualTo(-1), "the non-sliced address's storage row must be pruned exactly as it is today");
        }

        pruner.Dispose();
    }

    [Test]
    public void ReconcileSliceScopes_ForARemovedAddress_DeletesItsScopeAndMakesItPrunableAgain()
    {
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, SlicedAddress, block: 5, new Account(1, 100));
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);

        (HistoryAvailability availability, _, HistoryWindowPruner pruner) = CreateWriterAndPruner(retentionBlocks: 8, sliceAddresses: SlicedAddress.ToString());
        pruner.ReconcileSliceScopes();
        Assert.That(availability.ResolveScope(ScopeKeyOf(SlicedAddress)).IsGeneral, Is.False, "precondition: the scope exists after the first reconcile");
        pruner.Dispose();

        (HistoryAvailability sameAvailabilityViaNewInstance, _, HistoryWindowPruner secondPruner) = CreateWriterAndPruner(retentionBlocks: 8, sliceAddresses: null);
        secondPruner.ReconcileSliceScopes();

        Assert.That(sameAvailabilityViaNewInstance.ResolveScope(ScopeKeyOf(SlicedAddress)).IsGeneral, Is.True, "removing the address from the allow-list must delete its scope record");

        secondPruner.RunOnePass(CancellationToken.None);

        HistoryStoreV3 accountHistoryV3 = new(_historyColumns.GetColumnDb(FlatHistoryColumns.AccountHistory));
        Span<byte> buffer = stackalloc byte[256];
        int written = accountHistoryV3.TryGetValueBeforeNextChange(0, AccountKeyOf(SlicedAddress), buffer, out _);
        Assert.That(written, Is.EqualTo(-1), "once its scope is gone the address's row is prunable under the general floor exactly like any other address");

        secondPruner.Dispose();
    }

    [Test]
    public void ReconcileSliceScopes_ForAnExistingScope_NeverOverwritesItsFloor()
    {
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 20);
        (HistoryAvailability availability, _, HistoryWindowPruner pruner) = CreateWriterAndPruner(retentionBlocks: 8, sliceAddresses: SlicedAddress.ToString());
        pruner.ReconcileSliceScopes();
        Assert.That(availability.TryRaiseScopeFloor(ScopeKeyOf(SlicedAddress), 999), Is.True, "precondition: simulate the pruner having advanced this scope's floor since creation");
        pruner.Dispose();

        (HistoryAvailability sameAvailabilityViaNewInstance, _, HistoryWindowPruner secondPruner) = CreateWriterAndPruner(retentionBlocks: 8, sliceAddresses: SlicedAddress.ToString());
        secondPruner.ReconcileSliceScopes();

        Assert.That(sameAvailabilityViaNewInstance.ResolveScope(ScopeKeyOf(SlicedAddress)).Floor, Is.EqualTo(999UL), "reconciling an already-configured address on restart must never reset its floor");

        secondPruner.Dispose();
    }

    [Test]
    public void ReconcileSliceScopes_OnAnUnwindowedDatabase_ThrowsInvalidConfigurationException()
    {
        (_, _, HistoryWindowPruner pruner) = CreateWriterAndPruner(retentionBlocks: 0, sliceAddresses: SlicedAddress.ToString());

        Assert.Throws<InvalidConfigurationException>(() => pruner.ReconcileSliceScopes(),
            "a slice is meaningless on an unwindowed (v2) database - everything is already retained - and publishing one would stamp the windowed format onto v2 data");

        pruner.Dispose();
    }

    [Test]
    public void SliceScopeConfig_Parse_HandlesTrimmedEntriesEmptyTokensAndRetentionSuffix()
    {
        string raw = $" {SlicedAddress}: 1000 , ,{OtherSlicedAddress}";
        IReadOnlyList<SliceScopeEntry> entries = SliceScopeConfig.Parse(raw);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(entries.Count, Is.EqualTo(2), "empty tokens between commas must be silently skipped");
            Assert.That(entries[0].Address, Is.EqualTo(SlicedAddress));
            Assert.That(entries[0].RetentionBlocks, Is.EqualTo(1000UL));
            Assert.That(entries[1].Address, Is.EqualTo(OtherSlicedAddress));
            Assert.That(entries[1].RetentionBlocks, Is.Null, "an entry with no ':' suffix means unbounded intent, not zero retention");
        }
    }

    [Test]
    public void SliceScopeConfig_Parse_OnAMalformedAddress_ThrowsNamingTheToken()
    {
        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(
            () => SliceScopeConfig.Parse("not-an-address"))!;
        Assert.That(exception.Message, Does.Contain("not-an-address"));
    }

    [Test]
    public void SliceScopeConfig_Parse_OnAMalformedRetentionSuffix_ThrowsNamingTheToken()
    {
        string token = $"{SlicedAddress}:not-a-number";
        InvalidConfigurationException exception = Assert.Throws<InvalidConfigurationException>(
            () => SliceScopeConfig.Parse(token))!;
        Assert.That(exception.Message, Does.Contain(token));
    }

    [Test]
    public void TryGetAccount_AsOfBlockAcrossASelfDestructAndRedeploy_ResolvesCodeViaAccountHistoryAndTheCodeDb()
    {
        byte[] codeBeforeUpgrade = [0x60, 0x01];
        byte[] codeAfterUpgrade = [0x60, 0x02, 0x60, 0x03];
        Hash256 codeHashBefore = Keccak.Compute(codeBeforeUpgrade);
        Hash256 codeHashAfter = Keccak.Compute(codeAfterUpgrade);

        MemDb codeDb = new();
        codeDb.Set(codeHashBefore.Bytes, codeBeforeUpgrade);
        codeDb.Set(codeHashAfter.Bytes, codeAfterUpgrade);

        Account deployed = new(1, 100, Keccak.EmptyTreeHash, codeHashBefore);
        Account redeployed = new(1, 200, Keccak.EmptyTreeHash, codeHashAfter);

        HistoryColumnsWriter.RecordAccountV3(_historyColumns, SlicedAddress, block: 15, deployed);
        HistoryColumnsWriter.RecordAccountV3(_historyColumns, SlicedAddress, block: 16, account: null);
        HistoryColumnsWriter.SetWatermarkV3(_historyColumns, 30);
        HistoryColumnsWriter.SetPersistedAccount(_db, SlicedAddress, redeployed);

        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 1000 };
        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryReader reader = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.TryGetAccount(10, SlicedAddress, out AccountStruct beforeUpgrade), Is.True, "precondition: the account existed before the upgrade");
            Assert.That(codeDb[beforeUpgrade.CodeHash.Bytes], Is.EqualTo(codeBeforeUpgrade));

            Assert.That(reader.TryGetAccount(20, SlicedAddress, out AccountStruct afterUpgrade), Is.True, "precondition: the account exists again after the self-destruct and redeploy");
            Assert.That(codeDb[afterUpgrade.CodeHash.Bytes], Is.EqualTo(codeAfterUpgrade));

            Assert.That(reader.TryGetAccount(15, SlicedAddress, out _), Is.False, "as-of the self-destruct block itself, the account must not exist");
        }
    }

    private (HistoryAvailability Availability, HistoryWriter Writer, HistoryWindowPruner Pruner) CreateWriterAndPruner(ulong retentionBlocks, string? sliceAddresses)
    {
        FlatDbConfig config = new()
        {
            HistoryEnabled = true,
            HistoryRetentionBlocks = retentionBlocks,
            HistoryPruneIntervalBlocks = 1,
            HistorySliceAddresses = sliceAddresses,
        };

        (HistoryAvailability availability, HistoryRowFormat rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        HistoryWriter writer = new(_db, _historyColumns, config, availability, rowFormat, LimboLogs.Instance);
        HistoryWindowPruner pruner = new(
            writer, _historyColumns, config,
            new HistoryScopeGate(),
            availability, rowFormat,
            LimboLogs.Instance);

        return (availability, writer, pruner);
    }

    private static byte[] AccountKeyOf(Address address)
    {
        Span<byte> buffer = stackalloc byte[HistoryKeyLayout.AccountKeyLength];
        return HistoryKeyLayout.EncodeAccountKey(buffer, address.ToAccountPath).ToArray();
    }

    private static byte[] ScopeKeyOf(Address address) =>
        address.ToAccountPath.Bytes[..HistoryKeyLayout.ScopeKeyLength].ToArray();

    private static byte[] StorageKeyOf(Address address, in UInt256 slot)
    {
        ValueHash256 slotHash = ValueKeccak.Zero;
        StorageTree.ComputeKeyWithLookup(slot, ref slotHash);
        Span<byte> buffer = stackalloc byte[BaseFlatPersistence.StorageKeyLength];
        return BaseFlatPersistence.EncodeStorageKeyHashedWithShortPrefix(buffer, address.ToAccountPath, slotHash).ToArray();
    }
}
