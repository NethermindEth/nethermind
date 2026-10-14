// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using static Nethermind.State.Flat.Rust.Test.GoldenVectorGenerator;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The golden vectors in flatdb-core/tests/golden/persisted, which the cargo tests replay against the Rust persisted
/// snapshot tier: each scenario runs through the C# tier and records, after every step, the catalog rows, every file
/// of the tier and the observable fields of every catalogued snapshot, and the reads of a read-only bundle over it.
/// </summary>
/// <remarks>
/// Only public C# entry points are driven, on one thread and in a fresh directory: bases are converted with
/// <see cref="PersistedSnapshotLoader.ConvertAndRegister"/>, compactions call
/// <see cref="PersistedSnapshotCompactor.DoCompactSnapshot"/> and <see cref="PersistedSnapshotCompactor.DoCompactCompactSized"/>
/// directly, and a reload disposes the tier in the container's order and loads it again with
/// <see cref="PersistedSnapshotLoader.Load"/>. The snapshots are built from the records of
/// native/crates/flatdb-ffi/src/snapshot_stream.rs, which the vectors carry, so the replay builds the same native ones.
/// </remarks>
[Parallelizable(ParallelScope.All)]
public class PersistedGoldenVectorGenerator
{
    private const int Seed = 20261014;

    // Tables and blob ranges up to this size are recorded byte for byte, so the Rust reader can be run over C# bytes.
    private const int MaxInlineBytes = 16 * 1024;

    private static readonly string[] Scenarios = ["content", "rollover", "merge", "bloom0", "catalog"];

    [Test, Explicit("Rewrites the checked-in persisted golden vectors")]
    public async Task Generate_persisted_golden_vectors([ValueSource(nameof(Scenarios))] string scenario) =>
        await File.WriteAllTextAsync(GoldenFile(scenario), await Generate(scenario));

    [Test]
    public async Task Checked_in_persisted_golden_vectors_match_the_csharp_tier([ValueSource(nameof(Scenarios))] string scenario) =>
        Assert.That(
            await Generate(scenario),
            Is.EqualTo(await File.ReadAllTextAsync(GoldenFile(scenario))),
            $"The C# persisted tier drifted from the golden vector; regenerate with --filter \"FullyQualifiedName~{nameof(Generate_persisted_golden_vectors)}\" and port the change to Rust.");

    private static string GoldenFile(string scenario) => Path.Combine(GoldenVectorTests.GoldenDirectory(), "persisted", $"{scenario}.json");

    /// <summary>Returns the golden vector of one scenario as JSON, indented by one space, with a trailing newline.</summary>
    private static async Task<string> Generate(string scenario)
    {
        TierConfig config = scenario switch
        {
            "content" => new TierConfig(ArenaFileSizeBytes: 1024 * 1024, DedicatedArenaThresholdBytes: 1L << 30, BloomBitsPerKey: 14),
            "rollover" or "merge" => new TierConfig(ArenaFileSizeBytes: 64 * 1024, DedicatedArenaThresholdBytes: 16 * 1024, BloomBitsPerKey: 14),
            "bloom0" => new TierConfig(ArenaFileSizeBytes: 64 * 1024, DedicatedArenaThresholdBytes: 16 * 1024, BloomBitsPerKey: 0),
            "catalog" => new TierConfig(ArenaFileSizeBytes: 64 * 1024, DedicatedArenaThresholdBytes: 1L << 30, BloomBitsPerKey: 14),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        };

        using TempPath basePath = TempPath.GetTempDirectory();
        await using Recorder recorder = new(basePath.Path, config, inlineBytes: scenario is "content" or "merge");
        Scenario steps = new(recorder);
        await (scenario switch
        {
            "content" => steps.Content(),
            "rollover" => steps.Rollover(),
            "merge" or "bloom0" => steps.Merge(),
            "catalog" => steps.Catalog(),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null),
        });

        JsonObject vector = new() { ["scenario"] = scenario, ["config"] = config.ToJson(), ["steps"] = recorder.Steps };
        return vector.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 1, NewLine = "\n" }) + "\n";
    }

    /// <summary>The tier settings a scenario varies; the compaction schedule is the same for all.</summary>
    private sealed record TierConfig(long ArenaFileSizeBytes, long DedicatedArenaThresholdBytes, double BloomBitsPerKey)
    {
        private const ulong CompactSize = 4;
        private const ulong PersistedSnapshotMaxCompactSize = 8;

        public FlatDbConfig ToFlatDbConfig() => new()
        {
            ArenaFileSizeBytes = ArenaFileSizeBytes,
            PersistedSnapshotDedicatedArenaThresholdBytes = DedicatedArenaThresholdBytes,
            PersistedSnapshotBloomBitsPerKey = BloomBitsPerKey,
            PersistedSnapshotArenaPageCacheBytes = 0,
            PersistedSnapshotPunchHoleOnReclaim = false,
            CompactSize = CompactSize,
            PersistedSnapshotMaxCompactSize = PersistedSnapshotMaxCompactSize,
            CompactionOffset = 0,
        };

        public JsonObject ToJson()
        {
            FlatDbConfig flatConfig = ToFlatDbConfig();
            return new JsonObject
            {
                ["arenaFileSizeBytes"] = flatConfig.ArenaFileSizeBytes,
                ["dedicatedArenaThresholdBytes"] = flatConfig.PersistedSnapshotDedicatedArenaThresholdBytes,
                ["bloomBitsPerKey"] = flatConfig.PersistedSnapshotBloomBitsPerKey,
                ["arenaPageCacheBytes"] = flatConfig.PersistedSnapshotArenaPageCacheBytes,
                ["punchHoleOnReclaim"] = flatConfig.PersistedSnapshotPunchHoleOnReclaim,
                ["compactSize"] = flatConfig.CompactSize,
                ["persistedSnapshotMaxCompactSize"] = flatConfig.PersistedSnapshotMaxCompactSize,
                ["compactionOffset"] = flatConfig.CompactionOffset,
            };
        }
    }

    /// <summary>The C# persisted tier over one directory and catalog, wired as FlatWorldStateModule wires it.</summary>
    private sealed class Tier : IAsyncDisposable
    {
        public Tier(string basePath, IDb catalogDb, FlatDbConfig config)
        {
            string tierPath = Path.Combine(basePath, "persistedSnapshot");
            Arena = new ArenaManager(Path.Combine(tierPath, "arena"), config, LimboLogs.Instance);
            Blobs = new BlobArenaManager(Path.Combine(tierPath, "blob"), config.ArenaFileSizeBytes);
            Catalog = new SnapshotCatalog(catalogDb);
            Schedule = new CompactionSchedule(new MemDb(), config, LimboLogs.Instance);
            Repository = new SnapshotRepository(Arena, Blobs, Catalog, config, UnavailableStateHeaderProvider.Instance, LimboLogs.Instance);
            Loader = new PersistedSnapshotLoader(Repository, Arena, Blobs, Catalog, config, LimboLogs.Instance);
            Compactor = new PersistedSnapshotCompactor(
                Repository, Arena, Blobs, Catalog, config, Schedule, Loader, new ProcessExitSource(CancellationToken.None), LimboLogs.Instance);
        }

        public ArenaManager Arena { get; }
        public BlobArenaManager Blobs { get; }
        public SnapshotCatalog Catalog { get; }
        public CompactionSchedule Schedule { get; }
        public SnapshotRepository Repository { get; }
        public PersistedSnapshotLoader Loader { get; }
        public PersistedSnapshotCompactor Compactor { get; }

        /// <summary>Disposes in the container's order: the loader marks the tier's files for keeping before the repository drops its snapshots.</summary>
        public async ValueTask DisposeAsync()
        {
            await Compactor.DisposeAsync();
            Loader.Dispose();
            Repository.Dispose();
            Arena.Dispose();
            Blobs.Dispose();
        }
    }

    /// <summary>A snapshot read through the bundle above the persisted stack, from <see cref="From"/> to <see cref="To"/>.</summary>
    private sealed record InMemoryBlock(StateId From, StateId To, BlockPlan Plan);

    /// <summary>What a reads step reads: per address its account, self-destruct index and slots; then trie nodes.</summary>
    private sealed record ReadSet((Address Address, UInt256[] Slots)[] Accounts, TreePath[] StatePaths, (Hash256 Owner, TreePath Path)[] StorageNodes)
    {
        public ReadSet Concat(ReadSet other) =>
            new([.. Accounts, .. other.Accounts], [.. StatePaths, .. other.StatePaths], [.. StorageNodes, .. other.StorageNodes]);
    }

    /// <summary>Runs the steps against the tier and records each with the tier's state after it.</summary>
    private sealed class Recorder : IAsyncDisposable
    {
        private readonly string _basePath;
        private readonly TierConfig _config;
        private readonly bool _inlineBytes;
        private readonly MemDb _catalogDb = new();
        private readonly ResourcePool _resourcePool = new(new FlatDbConfig());
        // Every read the bundle passes down to the persistence misses.
        private readonly IPersistence _persistence = FlatTestDbs.CreateCSharpStack(new SnapshotableMemColumnsDb<FlatDbColumns>(), FlatLayout.Flat, carryForward: false);
        private readonly HashSet<string> _recordedKeys = [];
        private Tier _tier;

        public Recorder(string basePath, TierConfig config, bool inlineBytes)
        {
            _basePath = basePath;
            _config = config;
            _inlineBytes = inlineBytes;
            _tier = NewTier();
            _tier.Loader.Load();
        }

        public JsonArray Steps { get; } = [];

        public void Convert(in StateId from, in StateId to, BlockPlan plan)
        {
            long estimate;
            using (Snapshot snapshot = FlatArm.CSharp.Build(_resourcePool, plan, from, to, ResourcePool.Usage.MainBlockProcessing, out _))
            {
                estimate = PersistedSnapshotBuilder.EstimateSize(snapshot);
                _tier.Loader.ConvertAndRegister(snapshot);
            }

            Steps.Add(new JsonObject
            {
                ["kind"] = "convert",
                ["from"] = Json(from),
                ["to"] = Json(to),
                ["records"] = Hex(SnapshotRecords.Of(plan)),
                ["estimate"] = estimate,
                ["state"] = State(),
            });
        }

        /// <summary>
        /// Runs the compactions the batch worker of <see cref="PersistedSnapshotCompactor"/> runs for a batch holding
        /// only <paramref name="to"/>, in its order, with nothing persisted yet. A block whose window is a single
        /// snapshot is left out, as the compactor does nothing for it.
        /// </summary>
        public void CompactAsTheBatchWorker(in StateId to)
        {
            ulong block = to.BlockNumber;
            if (_tier.Schedule.IsLargeCompactionBoundary(block))
            {
                CompactSized(to);
                Compact(to);
            }
            else if (_tier.Schedule.IsCompactSizeBoundary(block)) CompactSized(to);
            else if (_tier.Schedule.GetPersistedSnapshotCompactSize(block) > 1) Compact(to);
        }

        private void Compact(in StateId to)
        {
            const ulong persistedBlock = 0;
            _tier.Compactor.DoCompactSnapshot(to, persistedBlock);
            Steps.Add(new JsonObject { ["kind"] = "compact", ["to"] = Json(to), ["persistedBlock"] = persistedBlock, ["state"] = State() });
        }

        private void CompactSized(in StateId to)
        {
            _tier.Compactor.DoCompactCompactSized(to);
            Steps.Add(new JsonObject { ["kind"] = "compactSized", ["to"] = Json(to), ["state"] = State() });
        }

        /// <summary>Disposes the tier as a shutdown does and loads it again; a load error ends the scenario.</summary>
        public async Task Reload()
        {
            await _tier.DisposeAsync();
            _tier = NewTier();
            try
            {
                _tier.Loader.Load();
            }
            catch (InvalidOperationException exception)
            {
                Steps.Add(new JsonObject { ["kind"] = "reload", ["error"] = exception.Message });
                return;
            }

            Steps.Add(new JsonObject { ["kind"] = "reload", ["state"] = State() });
        }

        /// <summary>Writes a row into the catalog database directly, as a foreign or corrupt row would be there.</summary>
        public void CatalogPut(byte[] key, byte[] value)
        {
            _catalogDb.Set(key, value);
            Steps.Add(new JsonObject { ["kind"] = "catalogPut", ["key"] = Hex(key), ["value"] = Hex(value) });
        }

        /// <summary>A copy of the catalog row at <paramref name="key"/>.</summary>
        public byte[] CatalogValue(byte[] key) => [.. _catalogDb.Get(key)!];

        /// <summary>Reads through a read-only bundle over the persisted snapshots assembled from <paramref name="stackHead"/> down to <paramref name="committed"/>, under <paramref name="inMemory"/>.</summary>
        public void Reads(in StateId stackHead, in StateId committed, InMemoryBlock[] inMemory, ReadSet reads)
        {
            AssembledSnapshotResult assembled = _tier.Repository.AssembleSnapshots(stackHead, committed, 16);
            assembled.InMemory.Dispose();
            PersistedSnapshotList persisted = assembled.Persisted;
            JsonArray stack = [.. persisted.Select(snapshot => (JsonNode)new JsonObject
            {
                ["from"] = Json(snapshot.From),
                ["to"] = Json(snapshot.To),
                ["tier"] = (int)CatalogTier(snapshot.From, snapshot.To),
            })];

            SnapshotPooledList snapshots = new(inMemory.Length);
            foreach (InMemoryBlock block in inMemory) snapshots.Add(FlatArm.CSharp.Build(_resourcePool, block.Plan, block.From, block.To, ResourcePool.Usage.ReadOnlyProcessingEnv, out _));

            JsonArray expect = [];
            using (IBundleView bundle = new FlatArm.CSharp.ReadOnlyView(new ReadOnlySnapshotBundle(
                       snapshots, _persistence.CreateReader(ReaderFlags.None), recordDetailedMetrics: false, new PersistedSnapshotStack(persisted, recordDetailedMetrics: false))))
            {
                foreach ((Address address, UInt256[] slots) in reads.Accounts)
                {
                    expect.Add(new JsonObject { ["op"] = "getAccount", ["address"] = Hex(address.Bytes), ["result"] = Json(bundle.GetAccount(address)) });
                    int selfDestructIdx = bundle.DetermineSelfDestructSnapshotIdx(address);
                    expect.Add(new JsonObject { ["op"] = "selfDestructIdx", ["address"] = Hex(address.Bytes), ["result"] = selfDestructIdx });
                    foreach (UInt256 slot in slots)
                    {
                        expect.Add(new JsonObject
                        {
                            ["op"] = "getSlot",
                            ["address"] = Hex(address.Bytes),
                            ["slot"] = Hex32(slot),
                            ["selfDestructIdx"] = selfDestructIdx,
                            ["result"] = bundle.GetSlot(address, slot, selfDestructIdx) is { } value ? Hex32(value) : null,
                        });
                    }
                }

                foreach (TreePath path in reads.StatePaths)
                {
                    expect.Add(new JsonObject { ["op"] = "loadStateRlp", ["path"] = Json(path), ["result"] = HexOrNull(bundle.LoadStateRlp(path)) });
                }

                foreach ((Hash256 owner, TreePath path) in reads.StorageNodes)
                {
                    expect.Add(new JsonObject
                    {
                        ["op"] = "loadStorageRlp",
                        ["addressHash"] = Hex(owner.Bytes),
                        ["path"] = Json(path),
                        ["result"] = HexOrNull(bundle.LoadStorageRlp(owner, path)),
                    });
                }
            }

            Steps.Add(new JsonObject
            {
                ["kind"] = "reads",
                ["stackHead"] = Json(stackHead),
                ["committed"] = Json(committed),
                ["stack"] = stack,
                ["inMemory"] = new JsonArray([.. inMemory.Select(static block => (JsonNode)new JsonObject
                {
                    ["from"] = Json(block.From),
                    ["to"] = Json(block.To),
                    ["records"] = Hex(SnapshotRecords.Of(block.Plan)),
                })]),
                ["expect"] = expect,
            });
        }

        public async ValueTask DisposeAsync()
        {
            await _tier.DisposeAsync();
            await ((IAsyncDisposable)_persistence).DisposeAsync();
        }

        /// <summary>
        /// The catalog rows in key order; every file of the tier with its length and SHA-256; and per catalogued
        /// snapshot its table's SHA-256, blob range and bloom, with the table and blob range bytes on the step that
        /// first records the snapshot when the scenario inlines them.
        /// </summary>
        private JsonObject State()
        {
            KeyValuePair<byte[], byte[]>[] rows = [.. _catalogDb.GetAll().OrderBy(static row => row.Key, Bytes.Comparer)];
            return new JsonObject
            {
                ["catalog"] = new JsonArray([.. rows.Select(static row => (JsonNode)Pair(row.Key, row.Value))]),
                ["files"] = Files(),
                ["snapshots"] = new JsonArray([.. _tier.Catalog.Load()
                    .Select(static entry => (Entry: entry, Key: CatalogKey(entry.To, entry.To.BlockNumber - entry.From.BlockNumber)))
                    .OrderBy(static entry => entry.Key, Bytes.Comparer)
                    .Select(entry => (JsonNode)SnapshotJson(entry.Key, entry.Entry))]),
            };
        }

        private JsonObject SnapshotJson(byte[] key, CatalogEntry entry)
        {
            if (!_tier.Repository.TryLeasePersistedState(entry.To, entry.Tier, out PersistedSnapshot? snapshot) || snapshot.From != entry.From)
                throw new InvalidOperationException($"Catalog entry {entry} is not in the repository");

            using (snapshot)
            {
                byte[] table = ReadFile(ArenaFile(entry.Location.ArenaId), entry.Location.Offset, entry.Location.Size);
                BlobRange blobRange = snapshot.BlobRange;
                JsonObject json = new()
                {
                    ["key"] = Hex(key),
                    ["table"] = Hex(SHA256.HashData(table)),
                    ["blobRange"] = new JsonObject { ["id"] = blobRange.BlobArenaId, ["offset"] = blobRange.Offset, ["length"] = blobRange.Length },
                    ["bloomCount"] = snapshot.Bloom.Count,
                    ["bloomDataBytes"] = snapshot.Bloom.DataBytes,
                };

                if (_recordedKeys.Add(Hex(key)) && _inlineBytes)
                {
                    if (table.Length <= MaxInlineBytes) json["tableHex"] = Hex(table);
                    if (blobRange.Length is > 0 and <= MaxInlineBytes) json["blobHex"] = Hex(ReadFile(BlobFile(blobRange.BlobArenaId), blobRange.Offset, blobRange.Length));
                }

                return json;
            }
        }

        private Tier NewTier() => new(_basePath, _catalogDb, _config.ToFlatDbConfig());

        private SnapshotTier CatalogTier(in StateId from, in StateId to)
        {
            foreach (CatalogEntry entry in _tier.Catalog.Load())
            {
                if (entry.From == from && entry.To == to) return entry.Tier;
            }

            throw new InvalidOperationException($"No catalog entry from {from} to {to}");
        }

        private JsonArray Files()
        {
            string tierPath = Path.Combine(_basePath, "persistedSnapshot");
            return new JsonArray([.. Directory.GetFiles(tierPath, "*", SearchOption.AllDirectories)
                .Order(StringComparer.Ordinal)
                .Select(path =>
                {
                    byte[] content = ReadFile(path, 0, new FileInfo(path).Length);
                    return (JsonNode)new JsonArray(Path.GetRelativePath(tierPath, path).Replace(Path.DirectorySeparatorChar, '/'), content.Length, Hex(SHA256.HashData(content)));
                })]);
        }

        /// <summary>The arena file of <paramref name="arenaId"/>, whichever of the three pools it was created in.</summary>
        private string ArenaFile(int arenaId) =>
            new[] { "arena", "small_arena", "dedicated" }
                .Select(prefix => Path.Combine(_basePath, "persistedSnapshot", "arena", $"{prefix}_{arenaId:D4}.bin"))
                .Single(File.Exists);

        private string BlobFile(ushort blobId) => Path.Combine(_basePath, "persistedSnapshot", "blob", $"blob_{blobId:D4}.bin");

        /// <summary>Reads past the tier's own handles, which share the file for reading and writing.</summary>
        private static byte[] ReadFile(string path, long offset, long length)
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] bytes = new byte[length];
            int read = RandomAccess.Read(handle, bytes, offset);
            return read == length ? bytes : throw new EndOfStreamException($"{path}: {read} of {length} bytes at {offset}");
        }
    }

    /// <summary>The catalog key of SnapshotCatalog: the target block big-endian, its root, then the depth big-endian.</summary>
    private static byte[] CatalogKey(in StateId to, ulong depth)
    {
        byte[] key = new byte[48];
        BinaryPrimitives.WriteUInt64BigEndian(key, to.BlockNumber);
        to.StateRoot.Bytes.CopyTo(key.AsSpan(8));
        BinaryPrimitives.WriteUInt64BigEndian(key.AsSpan(40), depth);
        return key;
    }

    /// <summary>The step streams of the scenarios.</summary>
    private sealed class Scenario(Recorder recorder)
    {
        private static readonly Address A = new("0xaaaaaaaa00000000000000000000000000000001");
        private static readonly Address B = new("0x00000000000000000000000000000000000000bb");
        private static readonly Address C = new("0xcccccccccccccccccccccccccccccccccccccccc");
        private static readonly Address D = new("0xdddddddddddddddddddddddddddddddddddddddd");
        private static readonly Address E = new("0xeeeeeeee00000000000000000000000000000000");
        private static readonly Address F = new("0xffffffff00000000000000000000000000000000");
        private static readonly Address G = new("0x1111111100000000000000000000000000000000");
        private static readonly Address H = new("0x2222222200000000000000000000000000000000");
        private static readonly Address X = new("0x5555555500000000000000000000000000000000");
        private static readonly Address Heavy = new("0x7777777777777777777777777777777777777777");
        private static readonly Address Absent = new("0x0000000000000000000000000000000000000404");

        private static readonly Hash256 HashA = new(A.ToAccountPath);
        // Shares the 20-byte prefix the storage-node keys keep, so its nodes sort between HashA's by path.
        private static readonly Hash256 HashATwin = SharesKeyPrefixWith(HashA);
        private static readonly Hash256 HashC = new(C.ToAccountPath);
        private static readonly Hash256 HashE = new(E.ToAccountPath);

        // Lengths 0-7 take the 4-byte key form, 8-15 the 8-byte form, 16+ the 32-byte form with the length.
        private static readonly TreePath[] StatePaths =
        [
            P(""), P("1"), P("12"), P("123"), P("1234"), P("12345"), P("123456"), P("1234567"),
            P("12345678"), P("123456789"), P("123456789abcde"), P("123456789abcdef"),
            P("123456789abcdef0"), P("123456789abcdef01"), P("123456789abcdef0123456789abcdef"),
            P("123456789abcdef0123456789abcdef0"), P("123456789abcdef0123456789abcdef01"),
            P("123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            P("123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0"),
            P("f"), P("fe"), P("fedcba98"),
            // Nibbles past the length are kept in the key, not masked.
            new TreePath(new ValueHash256("0x12345fff00000000000000000000000000000000000000000000000000000000"), 5),
            new TreePath(new ValueHash256("0xfedcba9876543fff000000000000000000000000000000000000000000000000"), 13),
        ];

        private static readonly TreePath[] StatePlaceholders = [P("a5"), P("a5a5a5a5a5")];

        private static readonly (Hash256 Owner, TreePath Path)[] StorageNodes =
        [
            (HashA, P("")), (HashA, P("3")), (HashA, P("7f")), (HashA, P("0123456789abcdef")), (HashA, P("80000000000000000000")),
            (HashA, P("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")),
            (HashATwin, P("4")), (HashATwin, P("7e")), (HashATwin, P("0123456789abcde")),
            (HashE, P("")), (HashE, P("1")),
        ];

        private static readonly (Hash256 Owner, TreePath Path)[] StoragePlaceholders = [(HashC, P("8"))];

        // Totals of 1 or 2, 20, 31, 32, 33, 62, 303 and 532 bytes: inline and hashed nodes, short and long headers.
        private static readonly int[] RlpPayloadLengths = [1, 19, 30, 31, 32, 60, 300, 529];

        private readonly Random _random = new(Seed);

        /// <summary>Every kind of entry, an empty snapshot and a table past 64 KiB, read alone and under in-memory snapshots, then reloaded.</summary>
        public async Task Content()
        {
            recorder.Convert(Block(0), Block(1), ContentPlan());
            recorder.Convert(Block(1), Block(2), BlockPlan.Empty);
            recorder.Convert(Block(2), Block(3), LargePlan(out ReadSet largeReads));

            ReadSet reads = ContentReads().Concat(largeReads);
            recorder.Reads(Block(3), Block(0), [], reads);
            recorder.Reads(Block(3), Block(0), [new InMemoryBlock(Block(3), Block(4), Block4Plan()), new InMemoryBlock(Block(4), Block(5), Block5Plan())], ContentReads());
            await recorder.Reload();
            recorder.Reads(Block(3), Block(0), [], reads);
        }

        /// <summary>Bases in the shared small arenas until one rolls over, one in a dedicated arena that also rolls the blob file over, and their compactions.</summary>
        public async Task Rollover()
        {
            const int bases = 12;
            for (ulong block = 1; block <= bases; block++)
            {
                // 18 nodes keep a base's estimate just under the 16 KiB dedicated threshold; 100 more put base 5 over it
                // and over what is left of the 64 KiB blob file.
                recorder.Convert(Block(block - 1), Block(block), SmallPlan(placeholderCount: block == 5 ? 113 : 13));
                recorder.CompactAsTheBatchWorker(Block(block));
            }

            ReadSet reads = new([(A, [1, 2, 3, 12])], [P("1"), P("2"), P("c")], []);
            recorder.Reads(Block(bases), Block(0), [], reads);
            await recorder.Reload();
            recorder.Reads(Block(bases), Block(0), [], reads);
            recorder.Reads(Block(7), Block(0), [], reads);
        }

        /// <summary>
        /// Windows of 2, 4 and 8 over bases that overwrite, delete and self-destruct the same addresses, with a base in
        /// a second blob file; read at heads inside the large window, where its shared bloom serves, then reloaded.
        /// Head 2 is read before the large window exists too: A's slot 4, written only below A's self-destruct, is
        /// not in the large window's bloom, which the windows below share once it does.
        /// </summary>
        public async Task Merge()
        {
            ReadSet reads = new(
                [(A, [1, 2, 3, 4, 5]), (B, []), (C, []), (D, [1, 2]), (X, [1])],
                [P("1"), P("2"), P("3"), P("a5")],
                [(HashA, P(""))]);
            BlockPlan[] plans = MergePlans();
            for (ulong block = 1; block <= (ulong)plans.Length; block++)
            {
                recorder.Convert(Block(block - 1), Block(block), plans[block - 1]);
                recorder.CompactAsTheBatchWorker(Block(block));
                if (block == 2) recorder.Reads(Block(2), Block(0), [], reads);
            }

            foreach (ulong head in (ulong[])[8, 6, 5, 2]) recorder.Reads(Block(head), Block(0), [], reads);
            await recorder.Reload();
            foreach (ulong head in (ulong[])[8, 2]) recorder.Reads(Block(head), Block(0), [], reads);
        }

        /// <summary>A base from pre-genesis, rows the load skips, then a row with a tier byte the load refuses.</summary>
        public async Task Catalog()
        {
            recorder.Convert(StateId.PreGenesis, Block(0), SmallPlan(placeholderCount: 0));
            recorder.Convert(Block(0), Block(1), SmallPlan(placeholderCount: 0));
            recorder.Convert(Block(1), Block(2), SmallPlan(placeholderCount: 0));

            byte[] blockOneKey = CatalogKey(Block(1), 1);
            recorder.CatalogPut(blockOneKey[..47], RandomBytes(101));
            recorder.CatalogPut(CatalogKey(Block(1), 5), RandomBytes(100));
            await recorder.Reload();
            recorder.Reads(Block(2), StateId.PreGenesis, [], new ReadSet([(A, [1, 2])], [P("1"), P("2")], []));

            byte[] value = recorder.CatalogValue(blockOneKey);
            value[^1] = (byte)SnapshotTier.InMemoryCompacted;
            recorder.CatalogPut(CatalogKey(Block(1), 7), value);
            await recorder.Reload();
        }

        private BlockPlan ContentPlan() => new(
            Accounts:
            [
                (A, new Account(1, 1000, RandomHash(), RandomHash())),
                (B, null),
                (C, new Account(0, 0)),
                (D, new Account(5, UInt256.MaxValue)),
                (E, new Account(1, 1)),
            ],
            Slots:
            [
                (A, 0, UInt256.Zero),
                (A, 1, new UInt256(Bytes.FromHexString("0x0000000000000000000000000000000000000000000000000000000000000100"), isBigEndian: true)),
                (A, 2, UInt256.MaxValue),
                (A, UInt256.MaxValue, 7),
                (A, 3, null),
                (E, 0, 9),
                (G, 1, RandomValue()),
                (G, 2, null),
                (H, 5, null),
            ],
            SelfDestructs: [(E, true), (F, false)],
            StateNodes:
            [
                .. StatePaths.Select((path, i) => StateNode(path, NodeRlp(i))),
                .. StatePlaceholders.Select(static path => StateNode(path, null)),
            ],
            StorageNodes:
            [
                .. StorageNodes.Select((node, i) => StorageNode(node.Owner, node.Path, NodeRlp(i + 3))),
                .. StoragePlaceholders.Select(static node => StorageNode(node.Owner, node.Path, null)),
            ]);

        private static ReadSet ContentReads() => new(
            [
                (A, [0, 1, 2, 3, 4, UInt256.MaxValue]), (B, []), (C, []), (D, []), (E, [0, 1]), (F, [0]), (G, [1, 2, 3]), (H, [5]),
                (Absent, [0]),
            ],
            [
                .. StatePaths, .. StatePlaceholders, P("999"),
                // The clean forms of the paths written with nibbles past their length.
                P("12345"), P("fedcba9876543"),
            ],
            [
                .. StorageNodes, .. StoragePlaceholders,
                // A storage-node key keeps 20 bytes of the address hash, so the twin reads HashA's node and HashA the twin's.
                (HashATwin, P("3")), (HashA, P("4")), (HashC, P("9")),
            ]);

        /// <summary>A slot-heavy account across several data blocks and accounts enough for a table past 64 KiB, so index values take 3 bytes.</summary>
        private BlockPlan LargePlan(out ReadSet reads)
        {
            UInt256[] slots = [.. Enumerable.Range(0, 300).Select(static i => (UInt256)(i * 7919 + 1))];
            Address[] accounts = [.. Enumerable.Range(0, 420).Select(_ => new Address(RandomBytes(20)))];
            reads = new ReadSet(
                [(Heavy, [slots[0], slots[150], slots[^1], 2]), .. accounts[..3].Select(static address => (address, Array.Empty<UInt256>()))],
                [],
                []);
            return new BlockPlan(
                Accounts:
                [
                    (Heavy, new Account(1, 1, RandomHash(), RandomHash())),
                    .. accounts.Select(address => (address, (Account?)new Account((ulong)_random.NextInt64(), RandomValue(), RandomHash(), RandomHash()))),
                ],
                Slots: [.. slots.Select(slot => (Heavy, slot, (UInt256?)RandomValue()))],
                SelfDestructs: [],
                StateNodes: [],
                StorageNodes: []);
        }

        /// <summary>Above the tier: A is destructed and re-created, so its tier slots read as deleted, and E gets a newer slot.</summary>
        private BlockPlan Block4Plan() => new(
            Accounts: [(A, new Account(4, 4))],
            Slots: [(E, 0, 77)],
            SelfDestructs: [(A, false)],
            StateNodes: [],
            StorageNodes: []);

        /// <summary>A slot of the destructed A, a re-created B, and a placeholder that falls through to the tier's node.</summary>
        private BlockPlan Block5Plan() => new(
            Accounts: [(B, new Account(2, 2))],
            Slots: [(A, 1, 5)],
            SelfDestructs: [],
            StateNodes: [StateNode(P("12"), NodeRlp(0)), StateNode(P("123"), null)],
            StorageNodes: [StorageNode(HashA, P("3"), NodeRlp(1))]);

        /// <summary>Two accounts, a slot of A, five nodes and <paramref name="placeholderCount"/> placeholders, which only raise the estimate.</summary>
        private BlockPlan SmallPlan(int placeholderCount) => new(
            Accounts: [(A, new Account(1, RandomValue())), (new Address(RandomBytes(20)), new Account(2, 2))],
            Slots: [(A, (UInt256)_random.Next(16), RandomValue())],
            SelfDestructs: [],
            StateNodes:
            [
                .. Enumerable.Range(0, 5).Select(i => StateNode(P(((byte)_random.Next(256)).ToString("x2")[..(1 + i % 2)]), NodeRlp(_random.Next(RlpPayloadLengths.Length)))),
                .. Enumerable.Range(0, placeholderCount).Select(static i => StateNode(P($"{i:x4}"), null)),
            ],
            StorageNodes: []);

        private BlockPlan[] MergePlans() =>
        [
            new(
                Accounts: [(A, new Account(1, 1)), (B, new Account(1, 0))],
                Slots: [(A, 1, RandomValue()), (A, 2, RandomValue()), (A, 3, RandomValue())],
                SelfDestructs: [],
                StateNodes: [StateNode(P("1"), NodeRlp(1)), StateNode(P("2"), NodeRlp(2))],
                StorageNodes: [StorageNode(HashA, P(""), NodeRlp(3))]),
            new(
                Accounts: [(A, new Account(2, 2)), (C, new Account(2, 0))],
                Slots: [(A, 1, null), (A, 4, RandomValue())],
                SelfDestructs: [],
                StateNodes: [StateNode(P("1"), NodeRlp(4)), StateNode(P("2"), NodeRlp(5))],
                StorageNodes: []),
            // The barrier: A's older slots are dropped from every window holding this base. The placeholders raise
            // the estimate past the dedicated threshold and the blob file's room, so its node lands in a second blob file.
            new(
                Accounts: [(A, new Account(3, 3))],
                Slots: [(A, 5, RandomValue())],
                SelfDestructs: [(A, false)],
                StateNodes: [StateNode(P("3"), NodeRlp(6)), .. Enumerable.Range(0, 100).Select(static i => StateNode(P($"a{i:x4}"), null))],
                StorageNodes: []),
            new(
                Accounts: [(B, null)],
                Slots: [(A, 3, RandomValue())],
                SelfDestructs: [],
                StateNodes: [],
                StorageNodes: [StorageNode(HashA, P(""), NodeRlp(7))]),
            new(
                Accounts: [(D, new Account(5, 5))],
                Slots: [(D, 1, RandomValue()), (A, 2, null)],
                SelfDestructs: [(D, true)],
                StateNodes: [StateNode(P("a5"), null)],
                StorageNodes: []),
            new(
                Accounts: [(C, new Account(6, 6))],
                Slots: [(X, 1, RandomValue())],
                SelfDestructs: [],
                StateNodes: [StateNode(P("1"), NodeRlp(0))],
                StorageNodes: []),
            new(
                Accounts: [],
                Slots: [(X, 1, null)],
                SelfDestructs: [(C, false)],
                StateNodes: [],
                StorageNodes: []),
            new(
                Accounts: [(A, new Account(8, 8))],
                Slots: [(D, 2, RandomValue())],
                SelfDestructs: [],
                StateNodes: [StateNode(P("2"), NodeRlp(1))],
                StorageNodes: []),
        ];

        private static StateId Block(ulong number) => new(number, Keccak.Compute(number.ToString()));

        private static TreePath P(string nibbles) => TreePath.FromHexString(nibbles);

        private static (TreePath Path, Hash256 Hash, byte[]? Rlp) StateNode(in TreePath path, byte[]? rlp) => (path, NodeHash(path, rlp), rlp);

        private static (Hash256 Owner, TreePath Path, Hash256 Hash, byte[]? Rlp) StorageNode(Hash256 owner, in TreePath path, byte[]? rlp) =>
            (owner, path, NodeHash(path, rlp), rlp);

        /// <summary>A node's hash, which the tier does not store: of its RLP, or for a placeholder of its path.</summary>
        private static Hash256 NodeHash(in TreePath path, byte[]? rlp) => Keccak.Compute(rlp ?? path.Path.ToByteArray());

        private static Hash256 SharesKeyPrefixWith(Hash256 hash)
        {
            byte[] bytes = hash.BytesToArray();
            for (int i = 20; i < bytes.Length; i++) bytes[i] ^= 0xff;
            return new Hash256(bytes);
        }

        /// <summary>A node's RLP: a byte string whose payload length is picked by <paramref name="index"/>.</summary>
        private byte[] NodeRlp(int index) => Rlp.Encode(RandomBytes(RlpPayloadLengths[index % RlpPayloadLengths.Length])).Bytes;

        private byte[] RandomBytes(int length)
        {
            byte[] bytes = new byte[length];
            _random.NextBytes(bytes);
            return bytes;
        }

        private Hash256 RandomHash() => new(RandomBytes(32));

        private UInt256 RandomValue() => new(RandomBytes(32), isBigEndian: true);
    }
}
