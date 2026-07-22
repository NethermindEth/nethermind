// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class FlatStoragePrefixScannerTests
{
    // Two vanity addresses sharing the 4-byte prefix 0x00000000, plus an unrelated one.
    private static readonly Address AddressA = new("0x0000000000000000000000000000000000000001");
    private static readonly Address AddressB = new("0x00000000ffffffffffffffffffffffffffffffff");
    private static readonly Address AddressC = new("0x1122334400000000000000000000000000000000");

    private SnapshotableMemColumnsDb<FlatDbColumns> _columnsDb = null!;
    private IPersistence _persistence = null!;

    [SetUp]
    public void SetUp()
    {
        _columnsDb = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _persistence = new PreimageRocksdbPersistence(_columnsDb, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown() => _columnsDb.Dispose();

    [Test]
    public void Scan_GroupsAddressesByPrefixAndMeasuresTheirStorage()
    {
        // Slot values are RLP-wrapped on write: 0x01 encodes to one byte, 0xff to two.
        WriteSlots((AddressA, 5, 0x01), (AddressB, 3, 0xff), (AddressC, 2, 0x01));
        // A key that is not a storage key must not be counted as a slot.
        _columnsDb.GetColumnDb(FlatDbColumns.Storage).Set(Bytes.FromHexString("0xdeadbeef"), [1]);

        FlatStoragePrefixScanner.Report report = Scan();

        Assert.Multiple(() =>
        {
            Assert.That(report.Completed, Is.True);
            Assert.That(report.TotalSlots, Is.EqualTo(10));
            Assert.That(report.TotalValueBytes, Is.EqualTo(5 + (3 * 2) + 2));
            Assert.That(report.SkippedKeys, Is.EqualTo(1));
            Assert.That(report.DistinctPrefixes, Is.EqualTo(2));
            Assert.That(report.DistinctAddresses, Is.EqualTo(3));
            Assert.That(report.CollidingPrefixes, Is.EqualTo(1));
            Assert.That(report.AddressesInCollidingPrefixes, Is.EqualTo(2));
            Assert.That(report.SlotsInCollidingPrefixes, Is.EqualTo(8));
            // One prefix with two addresses, one with a single address.
            Assert.That(report.AddressesPerPrefix, Is.EqualTo(new long[] { 1, 1, 0, 0, 0, 0, 0 }));
        });

        Assert.That(report.TopCollidingPrefixes, Has.Count.EqualTo(1));
        FlatStoragePrefixScanner.PrefixStats collidingPrefix = report.TopCollidingPrefixes[0];
        Assert.Multiple(() =>
        {
            Assert.That(collidingPrefix.Prefix, Is.EqualTo(0u));
            Assert.That(collidingPrefix.SlotCount, Is.EqualTo(8));
            Assert.That(collidingPrefix.ValueBytes, Is.EqualTo(5 + (3 * 2)));
            // Largest first, and each one pays for the whole bucket during iteration.
            AssertAddress(collidingPrefix.Addresses[0], AddressA, slotCount: 5, valueBytes: 5, amplification: 8d / 5);
            AssertAddress(collidingPrefix.Addresses[1], AddressB, slotCount: 3, valueBytes: 6, amplification: 8d / 3);
        });

        Assert.That(report.TopAddresses, Has.Count.EqualTo(3));
        Assert.Multiple(() =>
        {
            AssertAddress(report.TopAddresses[0], AddressA, slotCount: 5, valueBytes: 5, amplification: 8d / 5);
            AssertAddress(report.TopAddresses[1], AddressB, slotCount: 3, valueBytes: 6, amplification: 8d / 3);
            AssertAddress(report.TopAddresses[2], AddressC, slotCount: 2, valueBytes: 2, amplification: 1);
            Assert.That(report.ToString(), Does.Contain("0x00000000"));
        });
    }

    [Test]
    public void Scan_EmptyStorage_ReportsNothing()
    {
        FlatStoragePrefixScanner.Report report = Scan();

        Assert.Multiple(() =>
        {
            Assert.That(report.TotalSlots, Is.EqualTo(0));
            Assert.That(report.DistinctPrefixes, Is.EqualTo(0));
            Assert.That(report.CollidingPrefixes, Is.EqualTo(0));
            Assert.That(report.TopCollidingPrefixes, Is.Empty);
            Assert.That(report.TopAddresses, Is.Empty);
        });
    }

    private FlatStoragePrefixScanner.Report Scan() => FlatStoragePrefixScanner.Scan(
        (ISortedKeyValueStore)_columnsDb.GetColumnDb(FlatDbColumns.Storage),
        topCount: 50,
        LimboLogs.Instance.GetClassLogger<FlatStoragePrefixScannerTests>(),
        CancellationToken.None);

    private void WriteSlots(params (Address Address, int SlotCount, byte Value)[] accounts)
    {
        using IPersistence.IPersistenceReader reader = _persistence.CreateReader();
        using IPersistence.IWriteBatch batch = _persistence.CreateWriteBatch(reader.CurrentState, new StateId(1, Keccak.EmptyTreeHash), WriteFlags.DisableWAL);
        foreach ((Address address, int slotCount, byte value) in accounts)
        {
            for (int slot = 0; slot < slotCount; slot++)
            {
                batch.SetStorage(address, (UInt256)slot, SlotValue.FromSpanWithoutLeadingZero([value]));
            }
        }
    }

    private static void AssertAddress(FlatStoragePrefixScanner.AddressStats stats, Address expected, long slotCount, long valueBytes, double amplification)
    {
        Assert.That(stats.AddressKey, Is.EqualTo(expected.Bytes.ToArray()));
        Assert.That(stats.SlotCount, Is.EqualTo(slotCount));
        Assert.That(stats.ValueBytes, Is.EqualTo(valueBytes));
        Assert.That(stats.ScanAmplification, Is.EqualTo(amplification).Within(1e-9));
    }
}
