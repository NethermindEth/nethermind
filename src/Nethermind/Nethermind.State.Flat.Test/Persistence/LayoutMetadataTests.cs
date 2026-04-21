// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using NSubstitute;
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
    public void EnsureLayout_DoesNotWrite_OnFreshDb(FlatLayout configured)
    {
        using MemColumnsDb<FlatDbColumns> db = new();

        BasePersistence.EnsureLayout(db, configured);

        BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)).Should().BeNull();
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void EnsureLayout_IsNoOp_WhenStoredMatchesConfigured(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), layout);

        Assert.DoesNotThrow(() => BasePersistence.EnsureLayout(db, layout));

        BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)).Should().Be(layout);
    }

    [TestCase(FlatLayout.Flat, FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.FlatInTrie, FlatLayout.Flat)]
    [TestCase(FlatLayout.Flat, FlatLayout.PreimageFlat)]
    [TestCase(FlatLayout.PreimageFlat, FlatLayout.FlatInTrie)]
    public void EnsureLayout_Throws_WhenStoredDiffersFromConfigured(FlatLayout stored, FlatLayout configured)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), stored);

        InvalidConfigurationException ex = Assert.Throws<InvalidConfigurationException>(
            () => BasePersistence.EnsureLayout(db, configured))!;

        ex.Message.Should().Contain(stored.ToString());
        ex.Message.Should().Contain(configured.ToString());
    }

    [TestCase(FlatLayout.Flat)]
    [TestCase(FlatLayout.FlatInTrie)]
    [TestCase(FlatLayout.PreimageFlat)]
    public void LayoutRecordingPersistence_WritesLayout_OnFirstWriteBatch(FlatLayout layout)
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        IPersistence inner = Substitute.For<IPersistence>();
        inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>())
            .Returns(Substitute.For<IPersistence.IWriteBatch>());

        LayoutRecordingPersistence sut = new(inner, db, layout);

        BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)).Should().BeNull();

        using (sut.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), WriteFlags.None)) { }

        BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)).Should().Be(layout);
    }

    [Test]
    public void LayoutRecordingPersistence_DoesNotRewrite_OnSubsequentWriteBatches()
    {
        using MemColumnsDb<FlatDbColumns> db = new();
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), FlatLayout.Flat);

        IPersistence inner = Substitute.For<IPersistence>();
        inner.CreateWriteBatch(Arg.Any<StateId>(), Arg.Any<StateId>(), Arg.Any<WriteFlags>())
            .Returns(Substitute.For<IPersistence.IWriteBatch>());

        // constructor sees a stored layout and marks the flag as already persisted
        LayoutRecordingPersistence sut = new(inner, db, FlatLayout.Flat);

        // tamper with the stored layout to a different value, then run a batch:
        // the decorator must NOT overwrite because it already saw a stored value.
        BasePersistence.SetLayout(db.GetColumnDb(FlatDbColumns.Metadata), FlatLayout.FlatInTrie);

        using (sut.CreateWriteBatch(StateId.PreGenesis, new StateId(1, TestItem.KeccakA), WriteFlags.None)) { }

        BasePersistence.ReadLayout(db.GetColumnDb(FlatDbColumns.Metadata)).Should().Be(FlatLayout.FlatInTrie);
    }
}
