// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using BundleReads = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BundleReads;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// A flat DB one backend wrote reopens on any backend. Blocks are added on a RocksDB until the node shuts down, after a
/// flush or without one, which leaves the blocks above the newest state retained to be processed again; the DB then
/// reopens on the other backend, which must find the same persisted state, read the same through it and go on processing
/// blocks, every observation and the columns after reopening and at the end matching a run on the C# backend alone.
/// </summary>
/// <remarks>
/// With long finality, finality lags the head past the in-memory window, so the persisted-snapshot tier holds converted
/// and merged snapshots at shutdown, which the other backend opens: the states it retains, its reads at them and its
/// converted snapshots, as the C# tier reads them back, match too.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class CrossBackendRestartTests
{
    private const ulong SecondRunBlocks = 12;

    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;
    private static readonly FlatDbBackend[] TierBackends = [FlatDbBackend.CSharp, FlatDbBackend.Rust];
    private static readonly Address[] Addresses = [.. Enumerable.Range(1, 4).Select(static i => new Address($"0x{i:x40}"))];
    private static readonly Hash256[] Owners = [.. Addresses.Take(2).Select(static address => address.ToAccountPath.ToCommitment())];
    private static readonly UInt256[] Slots = [0, 1, 2, 255];
    private static readonly TreePath[] Paths =
        [.. new[] { "", "1", "12", "123456", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef" }.Select(TreePath.FromHexString)];

    [Test]
    public async Task Reopening_on_another_backend_continues_as_the_csharp_backend(
        [ValueSource(nameof(Layouts))] FlatLayout layout,
        [Values] FlatDbBackend writer,
        [Values] FlatDbBackend reader,
        [Values] bool flushBeforeShutdown) =>
        await Reopen(layout, new Side(writer, false, 0), new Side(reader, false, 0), flushBeforeShutdown);

    /// <summary>
    /// A flat DB written with long finality reopens with it on either backend, and without it on any backend, which
    /// leaves the tier's files untouched; one written without it reopens with an empty tier; and one whose tier was
    /// written with bloom filters reopens without them.
    /// </summary>
    [TestCaseSource(nameof(LongFinalityCases))]
    public async Task Reopening_with_long_finality_continues_as_the_csharp_backend(FlatLayout layout, Side writer, Side reader, bool flushBeforeShutdown) =>
        await Reopen(layout, writer, reader, flushBeforeShutdown);

    private static IEnumerable<TestCaseData> LongFinalityCases()
    {
        foreach (bool flush in new[] { false, true })
        {
            foreach (FlatDbBackend writer in TierBackends)
            {
                foreach (FlatDbBackend reader in TierBackends)
                {
                    foreach (FlatLayout layout in Layouts) yield return new TestCaseData(layout, Side.Tier(writer, 0), Side.Tier(reader, 0), flush);
                    yield return new TestCaseData(FlatLayout.Flat, Side.Tier(writer, 14), Side.Tier(reader, 0), flush);
                    yield return new TestCaseData(FlatLayout.Flat, new Side(writer, false, 0), Side.Tier(reader, 0), flush);
                }

                foreach (FlatDbBackend reader in Enum.GetValues<FlatDbBackend>())
                {
                    yield return new TestCaseData(FlatLayout.Flat, Side.Tier(writer, 0), new Side(reader, false, 0), flush);
                }
            }
        }
    }

    private static async Task Reopen(FlatLayout layout, Side writer, Side reader, bool flushBeforeShutdown)
    {
        if (writer.Backend != FlatDbBackend.CSharp || reader.Backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();

        List<string> expected = await Run(writer with { Backend = FlatDbBackend.CSharp }, reader with { Backend = FlatDbBackend.CSharp }, layout, flushBeforeShutdown);
        List<string> actual = await Run(writer, reader, layout, flushBeforeShutdown);

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(expected, Does.Contain($"head retained after reopen {flushBeforeShutdown}"), "only a flush persists the head");
        // A flush persists the head, which prunes the tier.
        if (writer.LongFinality && !flushBeforeShutdown)
        {
            Assert.That(expected, Has.Some.StartsWith("tier at shutdown: "), "the tier held converted snapshots at shutdown");
            if (reader.LongFinality) Assert.That(expected.Single(static line => line.StartsWith("held after reopen")).Split(','), Has.Length.GreaterThan(1), "states retained in the tier alone");
        }
    }

    /// <summary>The persisted states, the states retained, the reads, the columns and the converted snapshots of a run.</summary>
    private static async Task<List<string>> Run(Side writer, Side reader, FlatLayout layout, bool flushBeforeShutdown)
    {
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        List<string> observed = [];
        bool longFinality = writer.LongFinality || reader.LongFinality;
        // With long finality, finality lags past the in-memory window, so the tier holds converted bases and merges.
        ulong firstRunBlocks = longFinality ? 40UL : 20UL;
        ulong finalityLag = longFinality ? 16UL : 6UL;

        await using (FlatManagerContainer first = writer.Open(layout, path.Path, finality))
        {
            for (ulong number = 0; number < firstRunBlocks; number++) Add(first, finality, number, finalityLag, writer.LongFinality);
            if (flushBeforeShutdown) Flush(first, writer.LongFinality);
            observed.Add($"persisted at shutdown {first.PersistenceManager.GetCurrentPersistedStateId()}");
        }

        if (writer.LongFinality) observed.AddRange(PersistedDump.Take(path.Path, writer.Config(layout)).Select(static entry => $"tier at shutdown: {entry}"));
        List<string> tierFiles = TierFiles(path.Path);

        await using (FlatManagerContainer second = reader.Open(layout, path.Path, finality))
        {
            StateId persisted = second.PersistenceManager.GetCurrentPersistedStateId();
            ulong[] held = [.. Enumerable.Range(0, (int)firstRunBlocks).Select(static number => (ulong)number).Where(number => second.Arm.HasStateForBlock(Block(number)))];
            observed.Add($"persisted after reopen {persisted}");
            observed.Add($"head retained after reopen {second.Arm.HasStateForBlock(Block(firstRunBlocks - 1))}");
            observed.Add($"held after reopen {string.Join(',', held)}");
            observed.AddRange(Columns(second, "after reopen"));
            observed.AddRange(Reads(second, persisted));
            // The states above the persisted one are retained only in the tier.
            foreach (ulong number in held.Where(number => Block(number) != persisted)) observed.AddRange(Reads(second, Block(number)));

            // The blocks above the newest state retained are processed again, as the node does after a shutdown.
            for (ulong number = held.Length == 0 ? 0 : held[^1] + 1; number < firstRunBlocks + SecondRunBlocks; number++)
            {
                Add(second, finality, number, finalityLag, reader.LongFinality);
            }

            observed.AddRange(Reads(second, Block(firstRunBlocks + SecondRunBlocks - 1)));
            Flush(second, reader.LongFinality);
            observed.Add($"persisted at the end {second.PersistenceManager.GetCurrentPersistedStateId()}");
            observed.AddRange(Columns(second, "at the end"));
        }

        if (reader.LongFinality) observed.AddRange(PersistedDump.Take(path.Path, reader.Config(layout)).Select(static entry => $"tier at the end: {entry}"));
        else Assert.That(TierFiles(path.Path), Is.EqualTo(tierFiles), "a reader without long finality leaves the tier's files untouched");
        return observed;
    }

    /// <summary>Adds block <paramref name="number"/>, finalizes the block <paramref name="finalityLag"/> below it and persists what that allows.</summary>
    /// <remarks>
    /// With long finality the persisted-snapshot tier settles first: a merged snapshot keeps the storage trie nodes
    /// written before a self-destruct, so what a persist out of the tier writes depends on whether a merge ran.
    /// </remarks>
    private static void Add(FlatManagerContainer container, TestFinality finality, ulong number, ulong finalityLag, bool longFinality)
    {
        Plan(number).AddTo(container, number == 0 ? StateId.PreGenesis : Block(number - 1), Block(number));
        if (number > finalityLag + finality.FinalizedBlockNumber) finality.Finalize(number - finalityLag, Block);
        if (longFinality) NativeTier.WaitUntilSettled(() => container.TierFingerprint);
        container.PersistUpTo(Block(number));
    }

    /// <inheritdoc cref="Add" path="/remarks"/>
    private static void Flush(FlatManagerContainer container, bool longFinality)
    {
        if (longFinality) NativeTier.WaitUntilSettled(() => container.TierFingerprint);
        container.Arm.FlushCache(CancellationToken.None);
    }

    /// <summary>
    /// The writes of block <paramref name="number"/>: accounts set and deleted, slots set and cleared, self-destructs of
    /// new and existing accounts, inline and hashed trie nodes, and hash-only placeholders.
    /// </summary>
    private static BlockPlan Plan(ulong number)
    {
        Address address = Addresses[number % 4];
        return new BlockPlan(
            [(address, number % 5 == 4 ? null : new Account(number, number))],
            [(address, Slots[number % 3], number % 4 == 3 ? null : number + 1), (Addresses[(number + 1) % 4], 255, number)],
            number % 6 == 5 ? [(Addresses[(number + 2) % 4], number % 12 == 11)] : [],
            [(Paths[number % 5], Keccak.Compute($"state {number}"), number % 5 == 3 ? null : Rlp(number, number % 2 == 0 ? 3 : 40))],
            [(Owners[number % 2], Paths[(number + 1) % 5], Keccak.Compute($"storage {number}"), Rlp(number, 20))]);
    }

    /// <summary>An RLP list of <paramref name="length"/> bytes, as the tier reads a node back by the length its RLP declares.</summary>
    private static byte[] Rlp(ulong number, int length) =>
        [(byte)(0xc0 + length - 1), .. Enumerable.Range(0, length - 1).Select(i => (byte)(number + (ulong)i))];

    private static IEnumerable<string> Reads(FlatManagerContainer container, StateId state) =>
        BundleReads.Read(container.Arm, state, Addresses, Slots, Paths, Owners).Select(read => $"at {state}: {read}");

    private static IEnumerable<string> Columns(FlatManagerContainer container, string when) =>
        FlatTestDbs.DumpAll(container.Db).Select(entry => $"{when}: {entry}");

    /// <summary>Every file of the persisted-snapshot tier in <paramref name="basePath"/> but the catalog's, with its length and hash.</summary>
    private static List<string> TierFiles(string basePath)
    {
        string tier = Path.Combine(basePath, "persistedSnapshot");
        return Directory.Exists(tier)
            ? [.. Directory.EnumerateFiles(tier, "*.bin", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
                .Select(file => $"{Path.GetRelativePath(tier, file)} {SHA256.HashData(File.ReadAllBytes(file)).ToHexString()}")]
            : [];
    }

    /// <summary>How one run opens the flat DB: its backend, and whether with long finality and how many bloom bits per key the tier uses.</summary>
    public sealed record Side(FlatDbBackend Backend, bool LongFinality, double BloomBitsPerKey)
    {
        public static Side Tier(FlatDbBackend backend, double bloomBitsPerKey) => new(backend, true, bloomBitsPerKey);

        public FlatDbConfig Config(FlatLayout layout)
        {
            FlatDbConfig config = FlatManagerContainer.Config(Backend, layout, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: LongFinality);
            if (LongFinality) config.PersistedSnapshotBloomBitsPerKey = BloomBitsPerKey;
            return config;
        }

        internal FlatManagerContainer Open(FlatLayout layout, string basePath, TestFinality finality) =>
            new(Config(layout), basePath, finality, new FlatManagerContainer.Overrides { RocksDbCatalog = LongFinality });

        public override string ToString() => LongFinality ? $"{Backend} with long finality, bloom {BloomBitsPerKey}" : $"{Backend}";
    }
}
