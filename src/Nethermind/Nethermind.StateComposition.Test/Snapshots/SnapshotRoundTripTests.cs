// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Immutable;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.StateComposition.Data;
using Nethermind.StateComposition.Snapshots;
using NUnit.Framework;

namespace Nethermind.StateComposition.Test.Snapshots;

/// <summary>
/// Round-trips a <see cref="StateCompositionSnapshot"/> through the RLP decoder
/// to confirm the schema extension for <see cref="CumulativeSizeStats.CodeBytesTotal"/>
/// and <see cref="CumulativeSizeStats.SlotCountHistogram"/> survives persistence.
/// These two fields are the only ones that are not maintained incrementally —
/// losing them on restart would zero-out a live production metric until the
/// next scheduled full scan, which can be hours away.
/// </summary>
[TestFixture]
public class SnapshotRoundTripTests
{
    private static CumulativeSizeStats BuildStats(long codeBytes, ImmutableArray<long> slotHist) =>
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
    public void RoundTrip_PreservesCodeBytesTotal()
    {
        ImmutableArray<long> hist = [0, 5, 4, 3, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        CumulativeSizeStats stats = BuildStats(codeBytes: 987_654, slotHist: hist);
        StateCompositionSnapshot original = new(stats, BlockNumber: 1_000_000,
            StateRoot: Keccak.Compute("root"), DiffsSinceBaseline: 42, ScanBlockNumber: 999_000);

        StateCompositionSnapshot decoded = RoundTrip(original);

        Assert.That(decoded.Stats.CodeBytesTotal, Is.EqualTo(987_654));
    }

    [Test]
    public void RoundTrip_PreservesSlotCountHistogram()
    {
        ImmutableArray<long> hist = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768];
        CumulativeSizeStats stats = BuildStats(codeBytes: 1, slotHist: hist);
        StateCompositionSnapshot original = new(stats, 1, Keccak.Compute("r"), 0, 0);

        StateCompositionSnapshot decoded = RoundTrip(original);

        Assert.That(decoded.Stats.SlotCountHistogram, Is.EqualTo(hist));
    }

    [Test]
    public void RoundTrip_DefaultHistogram_DecodesAsZeroFilledLength16()
    {
        // Pre-first-scan warm-up edge case: stats.SlotCountHistogram is default.
        // The encoder writes 16 zero longs (no absent marker in the simplified
        // schema) and the decoder reconstructs them as a concrete length-16
        // array of zeros. This matches the Metrics fan-out contract, which
        // requires a length-16 array and does not defend against default.
        CumulativeSizeStats stats = BuildStats(codeBytes: 0, slotHist: default);
        StateCompositionSnapshot original = new(stats, 1, Keccak.Compute("r"), 0, 0);

        StateCompositionSnapshot decoded = RoundTrip(original);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Stats.CodeBytesTotal, Is.Zero);
            Assert.That(decoded.Stats.SlotCountHistogram.IsDefault, Is.False);
            Assert.That(decoded.Stats.SlotCountHistogram, Is.EqualTo(ImmutableArray.Create(new long[16])));
        }
    }
}
