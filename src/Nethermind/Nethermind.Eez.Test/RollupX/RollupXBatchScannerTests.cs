// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Eez.Follower;
using Nethermind.Eez.Follower.RollupX;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Eez.Test.RollupX;

/// <summary>
/// Two recorded eez.dev batches, posted by two transactions of one L1 block: their blobs share the block's slot.
/// </summary>
public class RollupXBatchScannerTests
{
    private const ulong RollupId = 1;
    private const ulong BlockNumber = 23_414_000;
    private static readonly Address Registry = new("0x96d6617629b90bdf275dd1d4888170ad5266fa33");
    private static readonly Hash256 BlockHash = Keccak.Compute("L1 block with two RollupX batches");

    private JsonDocument _replay = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _replay = JsonDocument.Parse(File.ReadAllText(StatelessFixtures.PathOf("rollupx/eezdev-699069", "replay-0-2250.json")));
        await KzgPolynomialCommitments.InitializeAsync();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown() => _replay.Dispose();

    [Test]
    public async Task Scan_TwoSettlingBatchesInOneSlot_ReadsAndVerifiesTheSlotOnce()
    {
        RecordedBatch first = Record(0, 0);
        RecordedBatch second = Record(6, 1);
        CountingBlobs blobs = new([.. first.SlotBlobs, .. second.SlotBlobs]);

        ScannedBatch[] scanned = await Scan(blobs, first, second);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(scanned, Has.Length.EqualTo(2), "both batches of the block are scanned");
            Assert.That(scanned.Select(static s => s.Settlement.IsEmpty), Is.All.False, "precondition: both batches settle us, so both need their blobs");
            Assert.That(blobs.Reads, Is.EqualTo(1), "the slot is read, and its commitments recomputed, once for both batches");
            Assert.That(scanned[0].Batch.DaStream.ToArray(), Is.EqualTo(first.Stream), "the first batch carries the stream of its own blobs");
            Assert.That(scanned[1].Batch.DaStream.ToArray(), Is.EqualTo(second.Stream), "the second batch carries the stream of its own blobs");
        }
    }

    [Test]
    public async Task Scan_SecondBatchBlobMissingFromTheSlot_IsIncompleteWithoutAnotherRead()
    {
        RecordedBatch first = Record(0, 0);
        RecordedBatch second = Record(6, 1);
        CountingBlobs blobs = new(first.SlotBlobs);

        Assert.That(async () => await Scan(blobs, first, second), Throws.TypeOf<L1SourceIncompleteException>(),
            "a blob the slot does not serve leaves the block for a later retry");
        Assert.That(blobs.Reads, Is.EqualTo(1), "a later batch selects from the slot already read in this scan");
    }

    [Test]
    public async Task Scan_EachScan_ReadsTheSlotAgain()
    {
        RecordedBatch first = Record(0, 0);
        RecordedBatch second = Record(6, 1);
        CountingBlobs blobs = new([.. first.SlotBlobs, .. second.SlotBlobs]);

        await Scan(blobs, first, second);
        await Scan(blobs, first, second);

        Assert.That(blobs.Reads, Is.EqualTo(2), "no blob is kept from one scan to the next");
    }

    private static Task<ScannedBatch[]> Scan(IL1BlobSource blobs, RecordedBatch first, RecordedBatch second)
    {
        IEezL1Api l1 = Substitute.For<IEezL1Api>();
        l1.GetLogs(Registry, RollupXBatchScanner.L2TxSkippedTopic, Arg.Any<Hash256?>(), Arg.Any<ulong>(), Arg.Any<ulong>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EezL1Log>());
        l1.GetTransactionByBlockHashAndIndex(BlockHash, first.Index, Arg.Any<CancellationToken>()).Returns(first.Transaction);
        l1.GetTransactionByBlockHashAndIndex(BlockHash, second.Index, Arg.Any<CancellationToken>()).Returns(second.Transaction);
        L1SettlingBlock block = new(BlockNumber, BlockHash, [first.BatchLog, second.BatchLog], [.. first.Roots, .. second.Roots]);
        RollupXBatchScanner scanner = new(l1, blobs, Registry, RollupId, LimboLogs.Instance);
        return scanner.Scan(block, new EezL1Block { Number = BlockNumber, Hash = BlockHash }, CancellationToken.None);
    }

    /// <summary>A recorded batch, moved to transaction <paramref name="index"/> of the shared block.</summary>
    private RecordedBatch Record(int batch, ulong index)
    {
        JsonElement record = _replay.RootElement.GetProperty("batches")[batch];
        JsonElement tx = record.GetProperty("transaction");
        ulong recordedIndex = Quantity(tx.GetProperty("index"));
        EezL1Transaction transaction = new()
        {
            Hash = new Hash256(tx.GetProperty("hash").GetString()!),
            Input = Bytes.FromHexString(tx.GetProperty("input").GetString()!),
            BlobVersionedHashes = [.. tx.GetProperty("blobVersionedHashes").EnumerateArray().Select(static h => new Hash256(h.GetString()!))],
        };
        List<SettledRoot> roots = [];
        foreach (JsonElement log in record.GetProperty("logs").EnumerateArray())
        {
            EezL1Log settled = new()
            {
                Topics = [.. log.GetProperty("topics").EnumerateArray().Select(static t => new Hash256(t.GetString()!))],
                Data = Bytes.FromHexString(log.GetProperty("data").GetString()!),
            };
            if (Quantity(log.GetProperty("transactionIndex")) == recordedIndex && settled.Topics[0] == L1BatchScanner.L2ExecutionPerformedTopic
                && L1BatchScanner.SettledRootOf(settled) is { } root)
            {
                roots.Add(new SettledRoot(BlockNumber, BlockHash, index, Quantity(log.GetProperty("logIndex")), root));
            }
        }

        byte[][] slotBlobs = [.. record.GetProperty("slotBlobs").EnumerateArray().Select(static b => Blob(b.GetString()!))];
        byte[][] carried = BlobSelection.Select(slotBlobs, transaction.BlobVersionedHashes)!;
        EezL1Log batchLog = new()
        {
            Address = Registry,
            Topics = [L1BatchScanner.BatchPostedTopic],
            Data = [],
            BlockNumber = BlockNumber,
            BlockHash = BlockHash,
            TransactionHash = transaction.Hash,
            TransactionIndex = index,
        };
        return new RecordedBatch(index, transaction, batchLog, [.. roots], slotBlobs, BlobStream.Unpack(carried));
    }

    private static ulong Quantity(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetUInt64() : Convert.ToUInt64(value.GetString(), 16);

    private static byte[] Blob(string trimmedHex)
    {
        byte[] blob = new byte[BlobStream.BytesPerBlob];
        string hex = trimmedHex[2..];
        Bytes.FromHexString(hex.Length % 2 == 0 ? hex : hex + "0").CopyTo(blob, 0);
        return blob;
    }

    private sealed record RecordedBatch(ulong Index, EezL1Transaction Transaction, EezL1Log BatchLog, SettledRoot[] Roots, byte[][] SlotBlobs, byte[] Stream);

    /// <summary>A beacon node serving one slot; every read recomputes the commitment of each of its blobs.</summary>
    private sealed class CountingBlobs(byte[][] slot) : IL1BlobSource
    {
        public int Reads { get; private set; }

        public Task<IReadOnlyDictionary<Hash256, byte[]>> GetSlotBlobs(EezL1Block block, CancellationToken token)
        {
            Reads++;
            return Task.FromResult<IReadOnlyDictionary<Hash256, byte[]>>(BlobSelection.ByVersionedHash(slot));
        }
    }
}
