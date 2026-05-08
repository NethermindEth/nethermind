// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System.Collections.Generic;
using System.Collections.Immutable;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Diff;
using Nethermind.StateComposition.Snapshots;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Snapshots;

/// <summary>
/// Round-trips a <see cref="StateCompositionSnapshot"/> through the RLP decoder
/// to confirm the schema extension for <see cref="CumulativeTrieStats.CodeBytesTotal"/>
/// and <see cref="CumulativeTrieStats.SlotCountHistogram"/> survives persistence.
/// These two fields are the only ones that are not maintained incrementally —
/// losing them on restart would zero-out a live production metric until the
/// next scheduled full scan, which can be hours away.
/// </summary>
[TestFixture]
public class SnapshotRoundTripTests
{
    // Uses the positional constructor directly (not TestDataBuilders.BuildStats) because
    // RoundTrip_DefaultHistogram_DecodesAsZeroFilledLength16 deliberately feeds `default`
    // into the encoder — the shared helper normalises default→zeros before encoding,
    // which would hide the edge case under test.
    private static CumulativeTrieStats BuildStats(long codeBytes, ImmutableArray<long> slotHist) =>
        new(
            AccountsTotal: 100,
            ContractsTotal: 20,
            StorageSlotsTotal: 500,
            AccountTrieBranches: 50,
            AccountTrieExtensions: 40,
            AccountTrieLeaves: 60,
            AccountTrieBytes: 10_000,
            StorageTrieBranches: 25,
            StorageTrieExtensions: 15,
            StorageTrieLeaves: 30,
            StorageTrieBytes: 5_000,
            ContractsWithStorage: 10,
            EmptyAccounts: 5)
        {
            CodeBytesTotal = codeBytes,
            SlotCountHistogram = slotHist,
        };

    private static StateCompositionSnapshot BuildSnapshot(
        CumulativeTrieStats stats,
        long blockNumber,
        Hash256 stateRoot,
        int diffsSinceBaseline = 0,
        long scanBlockNumber = 0,
        Dictionary<ValueHash256, long>? slotCountByAddress = null,
        Dictionary<ValueHash256, int>? codeHashRefcounts = null,
        Dictionary<ValueHash256, int>? codeHashSizes = null) =>
        new(
            stats,
            blockNumber,
            stateRoot,
            diffsSinceBaseline,
            scanBlockNumber,
            new CumulativeDepthStats(),
            slotCountByAddress ?? [],
            codeHashRefcounts ?? [],
            codeHashSizes ?? []);

    private static StateCompositionSnapshot RoundTrip(StateCompositionSnapshot original)
    {
        StateCompositionSnapshotDecoder decoder = StateCompositionSnapshotDecoder.Instance;
        int length = decoder.GetLength(original);
        byte[] buffer = new byte[length];
        RlpStream stream = new(buffer);
        decoder.Encode(stream, original);

        Rlp.ValueDecoderContext ctx = buffer.AsRlpValueContext();
        return decoder.Decode(ref ctx);
    }

    [Test]
    public void RoundTrip_PreservesAllFields()
    {
        ImmutableArray<long> hist = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768];
        CumulativeTrieStats stats = BuildStats(codeBytes: 987_654, slotHist: hist);

        ValueHash256 addr1 = Keccak.Compute("addr1").ValueHash256;
        ValueHash256 addr2 = Keccak.Compute("addr2").ValueHash256;
        Dictionary<ValueHash256, long> slotCounts = new()
        {
            [addr1] = 42,
            [addr2] = 1_000_000,
        };

        ValueHash256 codeA = Keccak.Compute("codeA").ValueHash256;
        ValueHash256 codeB = Keccak.Compute("codeB").ValueHash256;
        Dictionary<ValueHash256, int> refcounts = new() { [codeA] = 3, [codeB] = 1 };
        Dictionary<ValueHash256, int> sizes = new() { [codeA] = 1024, [codeB] = 2048 };

        StateCompositionSnapshot original = BuildSnapshot(stats,
            blockNumber: 1_000_000, stateRoot: Keccak.Compute("root"),
            diffsSinceBaseline: 42, scanBlockNumber: 999_000,
            slotCountByAddress: slotCounts,
            codeHashRefcounts: refcounts,
            codeHashSizes: sizes);

        StateCompositionSnapshot decoded = RoundTrip(original);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Stats.CodeBytesTotal, Is.EqualTo(987_654));
            Assert.That(decoded.Stats.SlotCountHistogram, Is.EqualTo(hist));
            Assert.That(decoded.BlockNumber, Is.EqualTo(1_000_000));
            Assert.That(decoded.DiffsSinceBaseline, Is.EqualTo(42));
            Assert.That(decoded.ScanBlockNumber, Is.EqualTo(999_000));

            Assert.That(decoded.SlotCountByAddress, Has.Count.EqualTo(2));
            Assert.That(decoded.SlotCountByAddress[addr1], Is.EqualTo(42));
            Assert.That(decoded.SlotCountByAddress[addr2], Is.EqualTo(1_000_000));

            Assert.That(decoded.CodeHashRefcounts[codeA], Is.EqualTo(3));
            Assert.That(decoded.CodeHashRefcounts[codeB], Is.EqualTo(1));
            Assert.That(decoded.CodeHashSizes[codeA], Is.EqualTo(1024));
            Assert.That(decoded.CodeHashSizes[codeB], Is.EqualTo(2048));
        }
    }

    [Test]
    public void RoundTrip_DefaultHistogram_DecodesAsZeroFilledLength16()
    {
        // Pre-first-scan warm-up edge case: stats.SlotCountHistogram is default.
        // The encoder writes 16 zero longs (no absent marker in the simplified
        // schema) and the decoder reconstructs them as a concrete length-16
        // array of zeros. This matches the Metrics fan-out contract, which
        // requires a length-16 array and does not defend against default.
        CumulativeTrieStats stats = BuildStats(codeBytes: 0, slotHist: default);
        StateCompositionSnapshot original = BuildSnapshot(stats, 1, Keccak.Compute("r"));

        StateCompositionSnapshot decoded = RoundTrip(original);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Stats.CodeBytesTotal, Is.Zero);
            Assert.That(decoded.Stats.SlotCountHistogram.IsDefault, Is.False);
            Assert.That(decoded.Stats.SlotCountHistogram, Is.EqualTo(ImmutableArray.Create(new long[16])));
        }
    }
}
