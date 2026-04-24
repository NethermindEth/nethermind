// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core.Exceptions;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test.Persistence;

[TestFixture]
public class LayoutMetadataTests
{
    [Test]
    public void ReadLayout_ReturnsNull_OnEmptyStore()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)).Should().BeNull();
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void SetLayout_Then_ReadLayout_RoundTrips(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);

        BasePersistence.SetLayout(metadata, layout);

        BasePersistence.ReadLayout(metadata).Should().Be(layout);
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void ValidateLayout_DoesNotThrow_OnFreshDb(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        Assert.DoesNotThrow(() => BasePersistence.ValidateLayout(db, layout));
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void ValidateLayout_DoesNotThrow_WhenStoredMatches(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), layout);
        Assert.DoesNotThrow(() => BasePersistence.ValidateLayout(db, layout));
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void ValidateLayoutReturnFlag_ReturnsZero(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.ValidateLayoutReturnFlag(db, layout).Should().Be(0);
    }

    [TestCase(FlatLayout.Flat, FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.FlatInTrie, FlatLayout.Flat)]
    [TestCase(FlatLayout.Flat, FlatLayout.PreimageFlat)]
    [TestCase(FlatLayout.FlatInTrie, FlatLayout.PreimageFlat)]
    [TestCase(FlatLayout.PreimageFlat, FlatLayout.Flat)]
    [TestCase(FlatLayout.PreimageFlat, FlatLayout.FlatInTrie)]
    public void ValidateLayout_Throws_WhenStoredDiffers(FlatLayout stored, FlatLayout configured)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), stored);

        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(
            () => BasePersistence.ValidateLayout(db, configured))!;

        ex.Message.Should().Contain(stored.ToString());
        ex.Message.Should().Contain(configured.ToString());
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void RecordLayoutOnFirstBatch_WritesAndFlipsFlag_WhenZero(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        int flag = 0;

        BasePersistence.RecordLayoutOnFirstBatch(metadata, ref flag, layout);

        flag.Should().Be(1);
        BasePersistence.ReadLayout(metadata).Should().Be(layout);
    }

    [Test]
    public void RecordLayoutOnFirstBatch_DoesNothing_WhenFlagAlreadySet()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IDb metadata = db.GetColumnDb(FlatDbColumns.Metadata);
        int flag = 1;

        BasePersistence.RecordLayoutOnFirstBatch(metadata, ref flag, FlatLayout.FlatInTrie);

        flag.Should().Be(1);
        BasePersistence.ReadLayout(metadata).Should().BeNull();
    }
}
