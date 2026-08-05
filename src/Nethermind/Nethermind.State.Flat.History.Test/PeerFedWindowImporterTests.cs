// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
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
    private static readonly UInt256 Slot2 = 2;

    private SnapshotableMemColumnsDb<FlatDbColumns> _db = null!;
    private SnapshotableMemColumnsDb<FlatHistoryColumns> _historyColumns = null!;
    private HistoryWindowPruner _pruner = null!;
    private HistoryWriter _writer = null!;
    private HistoryReader _reader = null!;

    [SetUp]
    public void SetUp()
    {
        _db = new SnapshotableMemColumnsDb<FlatDbColumns>();
        _historyColumns = new SnapshotableMemColumnsDb<FlatHistoryColumns>();
        _writer = new HistoryWriter(_db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, LimboLogs.Instance);
        _reader = new HistoryReader(_db, _historyColumns, LimboLogs.Instance);
        _pruner = new HistoryWindowPruner(
            _writer, _historyColumns,
            new FlatDbConfig { HistoryEnabled = true },
            NullBackfillInterlock.Instance,
            new HistoryScopeGate(),
            LimboLogs.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _pruner.Dispose();
        _db.Dispose();
        _historyColumns.Dispose();
    }

    [Test]
    public async Task ImportRangeAsync_PeerFedPathAlone_ImportsAccountAndStorageCorrectly()
    {
        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [(Slot1, [0xAA])])),
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [(Slot1, [0xBB])]), (AddrB, new Account(2, 50), [])),
        });

        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(1, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.TryGetAccount(2, AddrA, out AccountStruct addrAAt2), Is.True, "AddrA must resolve at block 2");
            Assert.That(addrAAt2.Nonce, Is.EqualTo(2UL));
            Assert.That(_reader.TryGetAccount(1, AddrA, out AccountStruct addrAAt1), Is.True, "AddrA must resolve at block 1");
            Assert.That(addrAAt1.Nonce, Is.EqualTo(1UL));
            Assert.That(_reader.TryGetAccount(2, AddrB, out AccountStruct addrBAt2), Is.True, "AddrB must resolve at block 2");
            Assert.That(addrBAt2.Balance, Is.EqualTo((UInt256)50));
            Assert.That(_reader.TryGetStorage(1, AddrA, Slot1, out SlotValue slot1At1), Is.True, "slot1 must resolve at block 1");
            Assert.That(slot1At1.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0xAA }));
            Assert.That(_reader.TryGetStorage(2, AddrA, Slot1, out SlotValue slot1At2), Is.True, "slot1 must resolve at block 2");
            Assert.That(slot1At2.AsReadOnlySpan.WithoutLeadingZeros().ToArray(), Is.EqualTo(new byte[] { 0xBB }));
        }
    }

    [Test]
    public async Task ImportRangeAsync_ProducesByteIdenticalAccountAndStorageRowsToForwardCapture()
    {
        SnapshotableMemColumnsDb<FlatHistoryColumns> reference = new();
        HistoryColumnsWriter.RecordAccount(reference, AddrA, 5, new Account(5, 500));
        HistoryColumnsWriter.RecordStorage(reference, AddrA, Slot2, 5, [0xCC]);

        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [5] = BuildBlockPayload((AddrA, new Account(5, 500), [(Slot2, [0xCC])])),
        });

        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(5, 5, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            AssertColumnsEqual(reference, _historyColumns, FlatHistoryColumns.AccountHistory);
            AssertColumnsEqual(reference, _historyColumns, FlatHistoryColumns.StorageHistory);
        }

        reference.Dispose();
    }

    [Test]
    public async Task ImportRangeAsync_DoesNotPublishConnectedRange_UntilTheAnchorIsReached()
    {
        FakeWindowImportSource partial = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
        });

        PeerFedWindowImporter importer = CreateImporter(partial);
        await importer.ImportRangeAsync(1, 5, CancellationToken.None);

        Assert.That(importer.IsBelowAnchorServable(1), Is.False,
            "the imported span never reached the requested anchor (block 5), so nothing below it may be served yet");

        FakeWindowImportSource rest = new(new Dictionary<ulong, byte[]>
        {
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [])),
            [3] = BuildBlockPayload((AddrA, new Account(3, 300), [])),
            [4] = BuildBlockPayload((AddrA, new Account(4, 400), [])),
            [5] = BuildBlockPayload((AddrA, new Account(5, 500), [])),
        });
        PeerFedWindowImporter resumed = CreateImporter(rest);
        await resumed.ImportRangeAsync(1, 5, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumed.IsBelowAnchorServable(1), Is.True, "once the range connects to the anchor, every block inside it is servable");
            Assert.That(resumed.IsBelowAnchorServable(5), Is.True, "the anchor block itself is servable");
            // Not just the servability marker: the row from the very first (never-reached-anchor) run must have
            // actually been flushed rather than silently discarded — this is what the pre-fix bug enshrined.
            Assert.That(_reader.TryGetAccount(1, AddrA, out AccountStruct addrAAt1), Is.True,
                "block 1's row must have been durably written even though that run never reached the anchor");
            Assert.That(addrAAt1.Nonce, Is.EqualTo(1UL));
        }
    }

    [Test]
    public async Task ImportRangeAsync_ForADisjointLaterRange_DoesNotShortCircuitOnAnUnrelatedCursor()
    {
        FakeWindowImportSource firstRange = new(new Dictionary<ulong, byte[]>
        {
            [10] = BuildBlockPayload((AddrA, new Account(10, 1000), [])),
            [11] = BuildBlockPayload((AddrA, new Account(11, 1100), [])),
        });
        PeerFedWindowImporter importer = CreateImporter(firstRange);
        await importer.ImportRangeAsync(10, 11, CancellationToken.None);

        Assert.That(importer.IsBelowAnchorServable(10), Is.True, "precondition: the first, unrelated range must have completed");

        // A genuinely different, earlier target: the persisted cursor from [10,11] must never be reused as if it
        // already covered this range — the pre-fix bug let exactly this short-circuit without ever touching the source.
        FakeWindowImportSource disjointRange = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrB, new Account(1, 100), [])),
            [2] = BuildBlockPayload((AddrB, new Account(2, 200), [])),
        });
        PeerFedWindowImporter secondImporter = CreateImporter(disjointRange);
        await secondImporter.ImportRangeAsync(1, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(secondImporter.IsBelowAnchorServable(1), Is.True, "the disjoint range must have actually been imported, not short-circuited");
            Assert.That(_reader.TryGetAccount(2, AddrB, out AccountStruct addrBAt2), Is.True, "the disjoint range's data must actually be present");
            Assert.That(addrBAt2.Nonce, Is.EqualTo(2UL));
        }
    }

    [Test]
    public async Task ImportRangeAsync_AssemblesABlockSplitAcrossMultipleChunks()
    {
        byte[] chunk0 = ChangesetChunkCodec.Encode([new ChangesetAccountEntry(AddrA, true,
            ((ReadOnlySpan<byte>)AccountDecoder.Slim.EncodeToArrayPoolSpan(new Account(3, 300))).ToArray(), [])]);
        byte[] chunk1 = ChangesetChunkCodec.Encode([new ChangesetAccountEntry(AddrB, true,
            ((ReadOnlySpan<byte>)AccountDecoder.Slim.EncodeToArrayPoolSpan(new Account(3, 30))).ToArray(), [])]);

        MultiChunkSource source = new(3, [chunk0, chunk1]);
        PeerFedWindowImporter importer = CreateImporter(source);
        await importer.ImportRangeAsync(3, 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(_reader.TryGetAccount(3, AddrA, out AccountStruct addrA), Is.True, "the first chunk's account must be applied");
            Assert.That(addrA.Balance, Is.EqualTo((UInt256)300));
            Assert.That(_reader.TryGetAccount(3, AddrB, out AccountStruct addrB), Is.True, "the second chunk's account must also be applied — proves both chunks of the split block were assembled");
            Assert.That(addrB.Balance, Is.EqualTo((UInt256)30));
        }
    }

    [Test]
    public void ImportRangeAsync_OnAMalformedPayload_ThrowsInvalidOperationException()
    {
        MalformedPayloadSource source = new();
        PeerFedWindowImporter importer = CreateImporter(source);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await importer.ImportRangeAsync(1, 1, CancellationToken.None),
            "a payload the codec cannot decode must surface as this importer's one fail-closed exception type, never whatever the decoder happens to throw");
    }

    [Test]
    public async Task ImportRangeAsync_WithATinyShardBudget_SpillsMidBatchAndStillProducesCorrectFinalState()
    {
        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [])),
            [3] = BuildBlockPayload((AddrA, new Account(3, 300), [])),
        });

        FlatDbConfig tinyBudget = new() { HistoryEnabled = true, HistoryImportShardCount = 1, HistoryImportShardBufferBudgetEntries = 2, HistoryImportBatchBlocks = 1000 };
        PeerFedWindowImporter importer = new(source, _db, _historyColumns, tinyBudget, _pruner, LimboLogs.Instance);
        await importer.ImportRangeAsync(1, 3, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.IsBelowAnchorServable(1), Is.True, "a budget-forced early flush must still let the import reach and publish the anchor");
            Assert.That(_reader.TryGetAccount(1, AddrA, out AccountStruct at1), Is.True);
            Assert.That(at1.Nonce, Is.EqualTo(1UL));
            Assert.That(_reader.TryGetAccount(3, AddrA, out AccountStruct at3), Is.True);
            Assert.That(at3.Nonce, Is.EqualTo(3UL));
        }
    }

    [Test]
    public async Task ImportRangeAsync_WhenVerificationPasses_ImportsNormally()
    {
        FakeWindowImportSource source = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [])),
        });

        ValueHash256 seed = ValueKeccak.Compute("seed"u8);
        TrueChainHashSource hashSource = new(source, seed);
        RejectingPeerSink sink = new();

        PeerFedWindowImporter importer = new(source, _db, _historyColumns,
            new FlatDbConfig { HistoryEnabled = true }, _pruner, LimboLogs.Instance, hashSource, sink, seed);
        await importer.ImportRangeAsync(1, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(importer.IsBelowAnchorServable(1), Is.True);
            Assert.That(sink.WasCalled, Is.False, "verification must pass silently for honest data — the peer sink is never consulted");
        }
    }

    [Test]
    public async Task ImportRangeAsync_WhenVerificationFails_BansSourceAndRecoversFromAlternate()
    {
        FakeWindowImportSource honest = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [])),
        });
        FakeWindowImportSource corrupted = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 999), [])), // wrong data from this "peer"
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [])),
        });

        ValueHash256 seed = ValueKeccak.Compute("seed"u8);
        TrueChainHashSource hashSource = new(honest, seed);
        RecordingPeerSink sink = new(honest);

        PeerFedWindowImporter importer = new(corrupted, _db, _historyColumns,
            new FlatDbConfig { HistoryEnabled = true }, _pruner, LimboLogs.Instance, hashSource, sink, seed);
        await importer.ImportRangeAsync(1, 2, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(sink.Banned, Contains.Item(corrupted), "the corrupted source must be banned");
            Assert.That(importer.IsBelowAnchorServable(1), Is.True, "recovery from the honest alternate must let the import complete");
            Assert.That(_reader.TryGetAccount(1, AddrA, out AccountStruct addrA), Is.True);
            Assert.That(addrA.Balance, Is.EqualTo((UInt256)100), "the corrupted value must never land in the DB — only the alternate's honest value");
        }
    }

    [Test]
    public void ImportRangeAsync_WhenVerificationFailsWithNoAlternate_ThrowsRatherThanWritingUnverifiedData()
    {
        FakeWindowImportSource honestReference = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
        });
        FakeWindowImportSource corrupted = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 999), [])),
        });

        ValueHash256 seed = ValueKeccak.Compute("seed"u8);
        TrueChainHashSource hashSource = new(honestReference, seed);
        NoAlternatePeerSink sink = new();

        PeerFedWindowImporter importer = new(corrupted, _db, _historyColumns,
            new FlatDbConfig { HistoryEnabled = true }, _pruner, LimboLogs.Instance, hashSource, sink, seed);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await importer.ImportRangeAsync(1, 1, CancellationToken.None));
        Assert.That(_reader.TryGetAccount(1, AddrA, out _), Is.False, "unverified (potentially corrupt) data must never be written");
    }

    [Test]
    public async Task ImportRangeAsync_ResumesFromThePersistedCursor_WithoutReimportingCompletedBatches()
    {
        FakeWindowImportSource firstHalf = new(new Dictionary<ulong, byte[]>
        {
            [1] = BuildBlockPayload((AddrA, new Account(1, 100), [])),
            [2] = BuildBlockPayload((AddrA, new Account(2, 200), [])),
        });

        FlatDbConfig singleBlockBatches = new() { HistoryEnabled = true, HistoryImportBatchBlocks = 1 };
        PeerFedWindowImporter importer = new(firstHalf, _db, _historyColumns, singleBlockBatches, _pruner, LimboLogs.Instance);
        await importer.ImportRangeAsync(1, 2, CancellationToken.None);

        PoisonedBelowBlock resumeSource = new(3, "block below the resume cursor must never be re-requested");
        PeerFedWindowImporter resumed = new(resumeSource, _db, _historyColumns, singleBlockBatches, _pruner, LimboLogs.Instance);
        await resumed.ImportRangeAsync(1, 2, CancellationToken.None);

        Assert.That(resumed.IsBelowAnchorServable(1), Is.True,
            "a fully-completed prior run must let a repeat call publish the connected range without touching the source again");
    }

    [Test]
    public void ImportRangeAsync_DetectsAChunkIndexGap_AndThrows()
    {
        GappedChunkIndexSource source = new();
        PeerFedWindowImporter importer = CreateImporter(source);

        Assert.ThrowsAsync<InvalidOperationException>(async () => await importer.ImportRangeAsync(1, 1, CancellationToken.None),
            "a non-contiguous chunk index for a block must fail closed, never silently apply a partial changeset");
    }

    private PeerFedWindowImporter CreateImporter(IWindowImportSource source) =>
        new(source, _db, _historyColumns, new FlatDbConfig { HistoryEnabled = true }, _pruner, LimboLogs.Instance);

    private static byte[] BuildBlockPayload(params (Address Address, Account? Account, (UInt256 Slot, byte[] Value)[] Storage)[] accounts)
    {
        List<ChangesetAccountEntry> entries = [];
        foreach ((Address address, Account? account, (UInt256 Slot, byte[] Value)[] storage) in accounts)
        {
            byte[] accountValue = account is null
                ? []
                : ((ReadOnlySpan<byte>)AccountDecoder.Slim.EncodeToArrayPoolSpan(account)).ToArray();

            List<ChangesetSlotEntry> slots = new(storage.Length);
            foreach ((UInt256 slot, byte[] value) in storage)
            {
                slots.Add(new ChangesetSlotEntry(slot, value));
            }

            entries.Add(new ChangesetAccountEntry(address, AccountChanged: true, accountValue, slots));
        }

        return ChangesetChunkCodec.Encode(entries);
    }

    private static void AssertColumnsEqual(
        SnapshotableMemColumnsDb<FlatHistoryColumns> reference,
        SnapshotableMemColumnsDb<FlatHistoryColumns> imported,
        FlatHistoryColumns column)
    {
        Dictionary<byte[], byte[]?> referenceRows = ToDictionary(reference.GetColumnDb(column));
        Dictionary<byte[], byte[]?> importedRows = ToDictionary(imported.GetColumnDb(column));

        Assert.That(importedRows.Count, Is.EqualTo(referenceRows.Count), $"{column} row count must match forward capture");
        foreach (KeyValuePair<byte[], byte[]?> row in referenceRows)
        {
            Assert.That(importedRows.TryGetValue(row.Key, out byte[]? importedValue), Is.True, $"{column} key missing from imported rows");
            Assert.That(importedValue, Is.EqualTo(row.Value), $"{column} value for a shared key must be byte-identical to forward capture");
        }
    }

    private static Dictionary<byte[], byte[]?> ToDictionary(IDb column)
    {
        Dictionary<byte[], byte[]?> result = new(Nethermind.Core.Extensions.Bytes.EqualityComparer);
        foreach (KeyValuePair<byte[], byte[]?> entry in column.GetAll())
        {
            result[entry.Key] = entry.Value;
        }

        return result;
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

    private sealed class PoisonedBelowBlock(ulong minimumRequestableBlock, string failureReason) : IWindowImportSource
    {
        public IAsyncEnumerable<WindowImportChunk> GetChangesetsAsync(ulong fromBlockInclusive, ulong toBlockInclusive, CancellationToken cancellationToken)
        {
            if (fromBlockInclusive < minimumRequestableBlock)
            {
                throw new InvalidOperationException(failureReason);
            }

            return EmptyAsync();
        }

        private static async IAsyncEnumerable<WindowImportChunk> EmptyAsync()
        {
            await Task.CompletedTask;
            yield break;
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

    private sealed class TrueChainHashSource(IWindowImportSource referenceSource, ValueHash256 seed) : IChangesetHashSource
    {
        public async ValueTask<ValueHash256> GetClaimedChainHashAsync(ulong block, CancellationToken cancellationToken)
        {
            List<BlockDigest> digests = [];
            KeccakHash? currentHash = null;
            ulong? currentBlock = null;

            await foreach (WindowImportChunk chunk in referenceSource.GetChangesetsAsync(0, block, cancellationToken))
            {
                if (currentBlock is null || chunk.Block != currentBlock.Value)
                {
                    currentBlock = chunk.Block;
                    currentHash = KeccakHash.Create();
                }

                currentHash!.Update(chunk.Payload.Span);

                if (chunk.IsLastChunkForBlock)
                {
                    byte[] digest = new byte[32];
                    currentHash.UpdateFinalTo(digest);
                    digests.Add(new BlockDigest(chunk.Block, new ValueHash256(digest)));
                }
            }

            return WindowImportVerifier.FoldAscending(digests, seed);
        }
    }

    private sealed class RejectingPeerSink : IImportPeerSink
    {
        public bool WasCalled { get; private set; }

        public void BanSource(IWindowImportSource source, string reason) => WasCalled = true;

        public bool TryGetAlternateSource(IWindowImportSource banned, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWindowImportSource? alternate)
        {
            WasCalled = true;
            alternate = null;
            return false;
        }
    }

    private sealed class RecordingPeerSink(IWindowImportSource alternate) : IImportPeerSink
    {
        public List<IWindowImportSource> Banned { get; } = [];

        public void BanSource(IWindowImportSource source, string reason) => Banned.Add(source);

        public bool TryGetAlternateSource(IWindowImportSource banned, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWindowImportSource? alternateSource)
        {
            alternateSource = alternate;
            return true;
        }
    }

    private sealed class NoAlternatePeerSink : IImportPeerSink
    {
        public void BanSource(IWindowImportSource source, string reason) { }

        public bool TryGetAlternateSource(IWindowImportSource banned, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IWindowImportSource? alternateSource)
        {
            alternateSource = null;
            return false;
        }
    }
}
