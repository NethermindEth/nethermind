// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

[TestFixture]
public class HistoryRowFormatTests
{
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;

    [SetUp]
    public void SetUp() => _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();

    [TearDown]
    public void TearDown() => _historyColumns.Dispose();

    private HistoryAvailability Availability() => new(_historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks));

    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    [TestCase(FlatLayout.PreimageFlatV1)]
    public void Resolve_WindowedOnALayoutThatCannotBackTheV3Fallback_Refuses(FlatLayout layout)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 100, Layout = layout };

        Assert.That(() => HistoryRowFormat.Resolve(Availability(), config), Throws.InstanceOf<InvalidConfigurationException>(),
            "a v3 read falls through to the live flat Account column, which this layout keys differently or never populates - every account unchanged since the queried block would read as absent instead of failing");
    }

    [Test]
    public void Resolve_WindowedOnTheFlatLayout_ResolvesV3()
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 100, Layout = FlatLayout.Flat };

        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(Availability(), config);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(rowFormat.IsV3, Is.True);
            Assert.That(rowFormat.FormatVersion, Is.EqualTo(HistoryAvailability.WindowedFormatVersion));
        }
    }

    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    [TestCase(FlatLayout.PreimageFlatV1)]
    public void Resolve_UnwindowedOnANonFlatLayout_IsStillAllowed(FlatLayout layout)
    {
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 0, Layout = layout };

        HistoryRowFormat rowFormat = HistoryRowFormat.Resolve(Availability(), config);

        Assert.That(rowFormat.IsV3, Is.False,
            "v2 never reads the live flat Account column, so the layout that column uses cannot make it answer wrongly - the guard must stay narrow");
    }

    [TestCase((byte)1)]
    [TestCase((byte)2)]
    [TestCase((byte)3)]
    public void VerifyFormat_AgainstADatabaseStampedByATruncatedKeyEpoch_Refuses(byte stampedVersion)
    {
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), 1, ValueKeccak.Zero, stampedVersion);
        }

        Assert.That(() => Availability().VerifyFormat(), Throws.InstanceOf<InvalidConfigurationException>(),
            "every format at or below 3 keys an account row by a truncated path; reading one with 32-byte seeks finds nothing and reports absence instead of failing");
    }

    [Test]
    public void VerifyFormat_AgainstThisEpochsOwnStamps_Accepts()
    {
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), 1, ValueKeccak.Zero, HistoryAvailability.FormatVersion);
        }

        Assert.That(() => Availability().VerifyFormat(), Throws.Nothing);
    }

    [Test]
    public void Resolve_WindowingConfiguredAgainstAnExistingV2Database_Refuses()
    {
        using (IColumnsWriteBatch<FlatHistoryColumns> batch = _historyColumns.StartWriteBatch())
        {
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), 1, ValueKeccak.Zero, HistoryAvailability.FormatVersion);
        }

        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 100, Layout = FlatLayout.Flat };

        Assert.That(() => HistoryRowFormat.Resolve(Availability(), config), Throws.InstanceOf<InvalidConfigurationException>(),
            "v2 rows are descending post-values; reading them with v3 forward-seeks answers wrongly instead of failing, so windowing an existing v2 database must refuse outright");
    }

    [Test]
    public void Resolve_WhenTheDatabaseIsAlreadyStampedWindowed_RefusesOnANonFlatLayoutEvenWithRetentionUnset()
    {
        Availability().PublishGlobalFloor(10);
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 0, Layout = FlatLayout.FlatInTrie };

        Assert.That(() => HistoryRowFormat.Resolve(Availability(), config), Throws.InstanceOf<InvalidConfigurationException>(),
            "unsetting the retention does not undo a windowed stamp, so it must not be a way to slip past the layout check");
    }
}
