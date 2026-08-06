// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.State.Flat.History.Test;

public class PeerFedWindowImporterTests
{
    private static readonly Address AddrA = TestItem.AddressA;
    private static readonly Address AddrB = TestItem.AddressB;
    private static readonly UInt256 Slot1 = 1;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryAvailability _availability = null!;
    private HistoryRowFormat _rowFormat = null!;
    private HistoryWindowPruner _pruner = null!;
    private HistoryWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        FlatDbConfig config = new() { HistoryEnabled = true, HistoryRetentionBlocks = 8 };
        (_availability, _rowFormat) = HistoryColumnsWriter.CreateSharedFormat(_historyColumns, config);
        _writer = new HistoryWriter(_db, _historyColumns, config, _availability, _rowFormat, LimboLogs.Instance);
        _pruner = new HistoryWindowPruner(
            _writer, _historyColumns, config, NullBackfillInterlock.Instance, new HistoryScopeGate(), _availability, _rowFormat, LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _pruner.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public void ImportRangeAsync_OnAnUnwindowedDatabase_RefusesWithoutTouchingTheSource()
    {
        SnapshotableMemColumnsDb<FlatHistoryColumns> unwindowedColumns = new();
        FlatDbConfig unwindowedConfig = new() { HistoryEnabled = true };
        (HistoryAvailability unwindowedAvailability, HistoryRowFormat unwindowedFormat) = HistoryColumnsWriter.CreateSharedFormat(unwindowedColumns, unwindowedConfig);
        HistoryWriter unwindowedWriter = new(_db, unwindowedColumns, unwindowedConfig, unwindowedAvailability, unwindowedFormat, LimboLogs.Instance);
        HistoryWindowPruner unwindowedPruner = new(
            unwindowedWriter, unwindowedColumns, unwindowedConfig, NullBackfillInterlock.Instance, new HistoryScopeGate(), unwindowedAvailability, unwindowedFormat, LimboLogs.Instance);

        PoisonedSource poisoned = new();
        PeerFedWindowImporter importer = new(poisoned, _db, unwindowedColumns, unwindowedConfig, unwindowedPruner, unwindowedAvailability, unwindowedFormat, LimboLogs.Instance);

        Assert.That(() => importer.ImportRangeAsync(1, 1, CancellationToken.None),
            Throws.InstanceOf<InvalidConfigurationException>(),
            "an unwindowed (v2) database must refuse rather than let import silently stamp it to the windowed format");
        Assert.That(poisoned.WasCalled, Is.False, "the refusal must happen before the source is ever touched");

        unwindowedPruner.Dispose();
        unwindowedColumns.Dispose();
    }

    [Test]
    public async Task ImportRangeAsync_WritesRowsByteIdenticalToForwardCapture()
    {
        // The acceptance gate: every row this importer writes for a wire-supplied pre-value must be exactly the
        // row forward capture (HistoryWriter's own v3 write path, staged here via HistoryColumnsWriter's
        // RecordAccountV3/RecordStorageV3 - the same encoders, called the same way) would have written for the
        // identical touch. Three blocks, two keys, an account with no prior touch and one with a chained history.
        Account addrAPreBlock1 = new(1, 100);
        Account addrAPreBlock2 = new(2, 200);
        byte[] slot1PreBlock1 = [0x01];
        byte[] slot1PreBlock2 = [0x02];
        Account addrBPreBlock3 = new(9, 900);

        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, addrAPreBlock1, [(Slot1, slot1PreBlock1)])),
            [2] = BuildBlockPayload((AddrA, addrAPreBlock2, [(Slot1, slot1PreBlock2)])),
            [3] = BuildBlockPayload((AddrB, addrBPreBlock3, [])),
        });

        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(1, 3, CancellationToken.None);

        using SnapshotableMemColumnsDb<FlatHistoryColumns> reference = new();
        HistoryColumnsWriter.RecordAccountV3(reference, AddrA, 1, addrAPreBlock1);
        HistoryColumnsWriter.RecordStorageV3(reference, AddrA, Slot1, 1, slot1PreBlock1);
        HistoryColumnsWriter.RecordAccountV3(reference, AddrA, 2, addrAPreBlock2);
        HistoryColumnsWriter.RecordStorageV3(reference, AddrA, Slot1, 2, slot1PreBlock2);
        HistoryColumnsWriter.RecordAccountV3(reference, AddrB, 3, addrBPreBlock3);

        using (Assert.EnterMultipleScope())
        {
            AssertColumnRowsEqual(reference, _historyColumns, FlatHistoryColumns.AccountHistory);
            AssertColumnRowsEqual(reference, _historyColumns, FlatHistoryColumns.StorageHistory);
        }
    }

    [Test]
    public async Task ImportRangeAsync_WithAnAccountThatHadNoPriorTouch_WritesAnEmptyPreValueRow()
    {
        // The gap this importer used to fail closed on before the wire carried pre-values directly: a key's
        // oldest touch within the requested range legitimately has no earlier touch anywhere - the account did
        // not exist before this block. The wire's own AccountPreValue is empty for exactly this case, and that is
        // now sufficient on its own: no chaining, no boundary to resolve.
        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, null, [])),
        });

        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(1, 1, CancellationToken.None);

        using SnapshotableMemColumnsDb<FlatHistoryColumns> reference = new();
        HistoryColumnsWriter.RecordAccountV3(reference, AddrA, 1, null);

        AssertColumnRowsEqual(reference, _historyColumns, FlatHistoryColumns.AccountHistory);
    }

    [Test]
    public async Task ImportRangeAsync_WithAnEmptyChangesetRange_CompletesAndPublishesConnectedRangeAndLowersFloor()
    {
        // A block can legitimately have zero state changes (ChangesetChunkCodec.EncodeChunked's own documented
        // "still writes chunk 0 for an empty entry list" case) — nothing is touched, and the pipeline runs all
        // the way through to publishing regardless.
        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload(),
            [2] = BuildBlockPayload(),
            [3] = BuildBlockPayload(),
        });

        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(1, 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.IsBelowAnchorServable(1), Is.True, "an empty-changeset range must still connect and publish");
            Assert.That(importer.IsBelowAnchorServable(3), Is.True);
            Assert.That(_availability.TryGetGlobalFloor(out ulong floor), Is.True, "publishing must lower the shared HistoryAvailability's floor, not a private instance's");
            Assert.That(floor, Is.EqualTo(1UL));
            Assert.That(HistoryColumnsWriter.GetStampedFormatVersion(_historyColumns), Is.EqualTo(HistoryAvailability.WindowedFormatVersion),
                "publishing a floor must stamp the windowed format version through the shared HistoryAvailability");
        }
    }

    [Test]
    public async Task ImportRangeAsync_LoweringTheFloor_NeverRaisesItPastAnAlreadyLowerExistingValue()
    {
        _availability.TryLowerGlobalFloor(1);

        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]> { [5] = BuildBlockPayload() });
        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(5, 5, CancellationToken.None);

        _availability.TryGetGlobalFloor(out ulong floor);
        Assert.That(floor, Is.EqualTo(1UL), "TryLowerGlobalFloor must never raise the floor back up toward this call's own (higher) range");
    }

    [Test]
    public void CollectRangeAsync_AssemblesABlockSplitAcrossMultipleChunksIntoOneEntryList()
    {
        byte[] chunk0 = ChangesetChunkCodec.Encode([new ChangesetAccountEntry(AddrA, true,
            ((ReadOnlySpan<byte>)AccountDecoder.Slim.EncodeToArrayPoolSpan(new Account(3, 300))).ToArray(), ReadOnlyMemory<byte>.Empty, [])]);
        byte[] chunk1 = ChangesetChunkCodec.Encode([new ChangesetAccountEntry(AddrB, true,
            ((ReadOnlySpan<byte>)AccountDecoder.Slim.EncodeToArrayPoolSpan(new Account(3, 30))).ToArray(), ReadOnlyMemory<byte>.Empty, [])]);

        MultiChunkSource source = new(3, [chunk0, chunk1]);
        PeerFedWindowImporter importer = CreateImporter(source);

        PeerFedWindowImporter.CollectedBatch batch = importer.CollectRangeAsync(source, 3, 3, CancellationToken.None).GetAwaiter().GetResult();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(batch.Touches, Has.Count.EqualTo(2), "both chunks' entries must be merged into one touch list, proving the split block was reassembled");
            Assert.That(batch.Touches, Has.Some.Matches<PeerFedWindowImporter.RawTouch>(t => t.FlatKey.SequenceEqual(FlatAccountKey(AddrA))));
            Assert.That(batch.Touches, Has.Some.Matches<PeerFedWindowImporter.RawTouch>(t => t.FlatKey.SequenceEqual(FlatAccountKey(AddrB))));
            Assert.That(batch.Digests, Has.Count.EqualTo(1), "one block, split across chunks, must still yield exactly one digest");
            Assert.That(batch.LastProcessedBlock, Is.EqualTo(3UL));
        }
    }

    [Test]
    public void CollectRangeAsync_DetectsABlockNumberGap_AndThrows()
    {
        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
            [3] = BuildBlockPayload((AddrA, new Account(3, 300), [])), // block 2 missing
        });
        PeerFedWindowImporter importer = CreateImporter(source);

        Assert.That(() => importer.CollectRangeAsync(source, 1, 3, CancellationToken.None).GetAwaiter().GetResult(),
            Throws.InvalidOperationException.With.Message.Contains("block gap"),
            "a missing block number must fail closed, never silently narrow what is later published as connected");
    }

    [Test]
    public void CollectRangeAsync_DetectsAChunkIndexGap_AndThrows()
    {
        GappedChunkIndexSource source = new();
        PeerFedWindowImporter importer = CreateImporter(source);

        Assert.That(() => importer.CollectRangeAsync(source, 1, 1, CancellationToken.None).GetAwaiter().GetResult(),
            Throws.InvalidOperationException.With.Message.Contains("chunk index"),
            "a non-contiguous chunk index for a block must fail closed, never silently apply a partial changeset");
    }

    [Test]
    public void CollectRangeAsync_WhenTheStreamMovesToANewBlockBeforeFinishingThePrevious_Throws()
    {
        UnfinishedBlockSource source = new();
        PeerFedWindowImporter importer = CreateImporter(source);

        Assert.That(() => importer.CollectRangeAsync(source, 1, 2, CancellationToken.None).GetAwaiter().GetResult(),
            Throws.InvalidOperationException.With.Message.Contains("before block"),
            "the stream advancing to a new block without a final chunk for the previous one must fail closed");
    }

    [Test]
    public void CollectRangeAsync_OnAMalformedPayload_ThrowsInvalidOperationException()
    {
        MalformedPayloadSource source = new();
        PeerFedWindowImporter importer = CreateImporter(source);

        Assert.That(() => importer.CollectRangeAsync(source, 1, 1, CancellationToken.None).GetAwaiter().GetResult(),
            Throws.InvalidOperationException,
            "a payload the codec cannot decode must surface as this importer's one fail-closed exception type, never whatever the decoder happens to throw");
    }

    private PeerFedWindowImporter CreateImporter(IWindowImportSource source) =>
        new(source, _db, _historyColumns, new FlatDbConfig { HistoryEnabled = true, HistoryRetentionBlocks = 8 }, _pruner, _availability, _rowFormat, LimboLogs.Instance);

    private static byte[] FlatAccountKey(Address address)
    {
        Span<byte> buffer = stackalloc byte[Nethermind.State.Flat.Persistence.BaseFlatPersistence.AccountKeyLength];
        return Nethermind.State.Flat.Persistence.BaseFlatPersistence.EncodeAccountKeyHashed(buffer, address.ToAccountPath).ToArray();
    }

    // Builds pre-values on the wire - the only field this importer reads (see AppendTouches) - so a caller
    // supplies each key's value BEFORE the change at this block, exactly like HistoryColumnsWriter's V3 helpers.
    // The post-value fields are left empty: nothing this importer does ever reads them.
    private static byte[] BuildBlockPayload(params (Address Address, Account? PreAccount, (UInt256 Slot, byte[] PreValue)[] Storage)[] accounts)
    {
        List<ChangesetAccountEntry> entries = [];
        foreach ((Address address, Account? preAccount, (UInt256 Slot, byte[] PreValue)[] storage) in accounts)
        {
            byte[] accountPreValue = preAccount is null
                ? []
                : ((ReadOnlySpan<byte>)AccountDecoder.Slim.EncodeToArrayPoolSpan(preAccount)).ToArray();

            List<ChangesetSlotEntry> slots = new(storage.Length);
            foreach ((UInt256 slot, byte[] preValue) in storage)
            {
                slots.Add(new ChangesetSlotEntry(slot, ReadOnlyMemory<byte>.Empty, preValue));
            }

            entries.Add(new ChangesetAccountEntry(address, AccountChanged: true, ReadOnlyMemory<byte>.Empty, accountPreValue, slots));
        }

        return ChangesetChunkCodec.Encode(entries);
    }

    private static void AssertColumnRowsEqual(IColumnsDb<FlatHistoryColumns> expectedColumns, IColumnsDb<FlatHistoryColumns> actualColumns, FlatHistoryColumns column)
    {
        Dictionary<byte[], byte[]?> expected = expectedColumns.GetColumnDb(column).GetAll(ordered: true)
            .ToDictionary(kv => kv.Key, kv => kv.Value, Bytes.EqualityComparer);
        Dictionary<byte[], byte[]?> actual = actualColumns.GetColumnDb(column).GetAll(ordered: true)
            .ToDictionary(kv => kv.Key, kv => kv.Value, Bytes.EqualityComparer);

        Assert.That(actual.Count, Is.EqualTo(expected.Count), $"{column} row count must match forward capture exactly");
        foreach ((byte[] key, byte[]? expectedValue) in expected)
        {
            Assert.That(actual.TryGetValue(key, out byte[]? actualValue), Is.True, $"{column} is missing a row forward capture would have written");
            Assert.That(actualValue, Is.EqualTo(expectedValue), $"{column} row value must be byte-identical to forward capture");
        }
    }

    private sealed class FakeWindowImportSource(Dictionary<ulong, byte[]> payloadsByBlock) : IWindowImportSource
    {
        public async IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(
            ulong fromBlockInclusive, ulong toBlockInclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            List<ulong> blocks = [.. payloadsByBlock.Keys];
            blocks.Sort();
            foreach (ulong block in blocks)
            {
                if (block < fromBlockInclusive || block > toBlockInclusive) continue;

                cancellationToken.ThrowIfCancellationRequested();
                yield return new WindowImportChunk(block, 0, true, payloadsByBlock[block]);
                await Task.Yield();
            }
        }
    }

    private sealed class PoisonedSource : IWindowImportSource
    {
        public bool WasCalled { get; private set; }

        public IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("must never be reached");
        }
    }

    private sealed class GappedChunkIndexSource : IWindowImportSource
    {
        public async IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(
            ulong fromBlockInclusive, ulong toBlockInclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            byte[] payload = ChangesetChunkCodec.Encode([]);
            yield return new WindowImportChunk(fromBlockInclusive, ChunkIndex: 2, IsLastChunkForBlock: true, payload);
            await Task.Yield();
        }
    }

    private sealed class UnfinishedBlockSource : IWindowImportSource
    {
        public async IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(
            ulong fromBlockInclusive, ulong toBlockInclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            byte[] payload = ChangesetChunkCodec.Encode([]);
            yield return new WindowImportChunk(fromBlockInclusive, ChunkIndex: 0, IsLastChunkForBlock: false, payload);
            await Task.Yield();
            yield return new WindowImportChunk(fromBlockInclusive + 1, ChunkIndex: 0, IsLastChunkForBlock: true, payload);
            await Task.Yield();
        }
    }

    private sealed class MultiChunkSource(ulong block, byte[][] chunkPayloads) : IWindowImportSource
    {
        public async IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(
            ulong fromBlockInclusive, ulong toBlockInclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (block < fromBlockInclusive || block > toBlockInclusive) yield break;

            for (int i = 0; i < chunkPayloads.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new WindowImportChunk(block, (uint)i, i == chunkPayloads.Length - 1, chunkPayloads[i]);
                await Task.Yield();
            }
        }
    }

    private sealed class MalformedPayloadSource : IWindowImportSource
    {
        public async IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(
            ulong fromBlockInclusive, ulong toBlockInclusive, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            byte[] malformed = [0x7F, 0xFF, 0xFF, 0xFF]; // declares a huge entry count with no data behind it
            yield return new WindowImportChunk(fromBlockInclusive, 0, true, malformed);
            await Task.Yield();
        }
    }
}
