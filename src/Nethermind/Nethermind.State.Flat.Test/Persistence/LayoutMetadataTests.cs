// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class LayoutMetadataTests
{
    private static IEnumerable<FlatLayout> Layouts() => Enum.GetValues<FlatLayout>();

    /// <summary>Every ordered pair of distinct layouts, so a newly added layout is covered without further edits.</summary>
    private static IEnumerable<object[]> LayoutMismatches()
    {
        foreach (FlatLayout stored in Layouts())
            foreach (FlatLayout configured in Layouts())
                if (stored != configured)
                    yield return [stored, configured];
    }

    [Test]
    public void ReadLayout_ReturnsNull_OnEmptyStore()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        Assert.That(BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)), Is.Null);
    }

    [TestCaseSource(nameof(Layouts))]
    public void SetLayout_Then_ReadLayout_RoundTrips(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);

        BasePersistence.SetLayout(metadata, layout);

        Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(layout));
    }

    [Test]
    public void SetLayout_AlsoRecords_RlpSlotEncoding()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);

        BasePersistence.SetLayout(metadata, FlatLayout.Flat);

        Assert.That(metadata.Get(Keccak.Compute("SlotEncoding").BytesToArray()),
            Is.EqualTo(new[] { BasePersistence.SlotEncodingRlp }));
    }

    [TestCaseSource(nameof(Layouts))]
    public void ValidateLayout_DoesNotThrow_OnFreshDb(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        Assert.DoesNotThrow(() => BasePersistence.ValidateLayout(db, layout));
    }

    [TestCaseSource(nameof(Layouts))]
    public void ValidateLayout_DoesNotThrow_WhenStoredMatches(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), layout);
        Assert.DoesNotThrow(() => BasePersistence.ValidateLayout(db, layout));
    }

    [TestCaseSource(nameof(Layouts))]
    public void ValidateLayoutReturnFlag_ReturnsZero(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        Assert.That(BasePersistence.ValidateLayoutReturnFlag(db, layout), Is.EqualTo(0));
    }

    [TestCaseSource(nameof(LayoutMismatches))]
    public void ValidateLayout_Throws_WhenStoredDiffers(FlatLayout stored, FlatLayout configured)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), stored);

        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(
            () => BasePersistence.ValidateLayout(db, configured))!;

        Assert.That(ex.Message, Does.Contain(stored.ToString()));
        Assert.That(ex.Message, Does.Contain(configured.ToString()));
    }

    [TestCaseSource(nameof(Layouts))]
    public void RecordLayoutOnFirstBatch_WritesAndFlipsFlag_WhenZero(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        int flag = 0;

        BasePersistence.RecordLayoutOnFirstBatch(metadata, ref flag, layout);

        Assert.That(flag, Is.EqualTo(1));
        Assert.That(BasePersistence.ReadLayout(metadata), Is.EqualTo(layout));
    }

    [Test]
    public void RecordLayoutOnFirstBatch_DoesNothing_WhenFlagAlreadySet()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        int flag = 1;

        BasePersistence.RecordLayoutOnFirstBatch(metadata, ref flag, FlatLayout.FlatInTrie);

        Assert.That(flag, Is.EqualTo(1));
        Assert.That(BasePersistence.ReadLayout(metadata), Is.Null);
    }
}
