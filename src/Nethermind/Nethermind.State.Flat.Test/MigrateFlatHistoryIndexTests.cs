// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State.Flat.History;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.Flat.Test;

// Throwaway: covers the one-shot MigrateFlatHistoryIndex; delete with the migration step.
public class MigrateFlatHistoryIndexTests
{
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
    public async Task Migrates_legacy_markers_to_watermark_with_canonical_roots()
    {
        for (ulong block = 0; block <= 3; block++) WriteLegacyMarker(block);

        await Migrate(CanonicalUpTo(3)).Execute(CancellationToken.None);

        HistoryReader reader = new(_db, _historyColumns, LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            for (ulong block = 0; block <= 3; block++)
                Assert.That(reader.IsAvailable(new StateId(block, RootFor(block))), Is.True, $"block {block} migrated with its canonical root");
            Assert.That(reader.IsAvailable(new StateId(3, TestItem.KeccakA)), Is.False, "a non-canonical root at a migrated height is rejected");
        }
    }

    [Test]
    public async Task Migration_stops_at_the_first_gap()
    {
        WriteLegacyMarker(0);
        WriteLegacyMarker(1);
        WriteLegacyMarker(2);
        WriteLegacyMarker(4); // gap at block 3

        await Migrate(CanonicalUpTo(4)).Execute(CancellationToken.None);

        HistoryReader reader = new(_db, _historyColumns, LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reader.HasHistoryForBlock(2), Is.True);
            Assert.That(reader.HasHistoryForBlock(3), Is.False, "the gap fails closed");
            Assert.That(reader.HasHistoryForBlock(4), Is.False, "a marker above the gap is not covered");
        }
    }

    [Test]
    public async Task Migration_is_a_noop_on_an_empty_index()
    {
        await Migrate(CanonicalUpTo(0)).Execute(CancellationToken.None);

        Assert.That(new HistoryReader(_db, _historyColumns, LimboLogs.Instance).HasHistoryForBlock(0), Is.False);
    }

    private MigrateFlatHistoryIndex Migrate(IBlockTree blockTree) => new(_historyColumns, blockTree, LimboLogs.Instance);

    private static IBlockTree CanonicalUpTo(ulong maxBlock)
    {
        IBlockTree blockTree = Substitute.For<IBlockTree>();
        for (ulong block = 0; block <= maxBlock; block++)
        {
            blockTree.FindHeader(block)
                .Returns(Build.A.BlockHeader.WithNumber(block).WithStateRoot(new Hash256(RootFor(block))).TestObject);
        }
        return blockTree;
    }

    private void WriteLegacyMarker(ulong block)
    {
        Span<byte> key = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64BigEndian(key, block);
        _historyColumns.GetColumnDb(FlatHistoryColumns.AvailableBlocks).Set(key, Array.Empty<byte>());
    }

    private static ValueHash256 RootFor(ulong block)
    {
        Span<byte> root = stackalloc byte[32];
        root[0] = (byte)(block + 1); // non-zero so distinct per block and never the zero root
        return new ValueHash256(root);
    }
}
