// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using BundleReads = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BundleReads;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// A chain whose finality stalls at block <see cref="FinalizedBlock"/>, so long finality converts its base snapshots
/// past the in-memory window into the persisted-snapshot tier. Block n sets the account at
/// <see cref="FlatManagerContainer.BlockAddress"/>, its slot 1 and the state trie node at <see cref="NodePath"/> to
/// values of n, so every state reads the values of its own block.
/// </summary>
internal static class StalledChain
{
    public const ulong FinalizedBlock = 2;

    public static readonly TreePath NodePath = TreePath.FromHexString("12");

    private static readonly UInt256 Slot = 1;

    /// <summary>The long-finality config of the tests, on the flat layout; the catalog must be a RocksDB to reopen.</summary>
    public static FlatDbConfig Config(FlatDbBackend backend) =>
        FlatManagerContainer.Config(backend, FlatLayout.Flat, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: true);

    /// <summary>Adds the blocks <paramref name="first"/> to <paramref name="last"/>, persisting what finality allows after each.</summary>
    public static void Add(FlatManagerContainer container, TestFinality finality, ulong first, ulong last)
    {
        for (ulong number = first; number <= last; number++)
        {
            AddBlock(container, number);
            if (number == FinalizedBlock + 2) finality.Finalize(FinalizedBlock, Block);
            container.PersistUpTo(Block(number));
        }
    }

    /// <summary>Adds block <paramref name="number"/> on its parent, persisting nothing.</summary>
    public static void AddBlock(FlatManagerContainer container, ulong number) =>
        new BlockPlan(
                [(BlockAddress, new Account(number, number))],
                [(BlockAddress, Slot, number + 1)],
                [],
                [(NodePath, Keccak.Compute($"node {number}"), Rlp(number))],
                [])
            .AddTo(container, number == 0 ? StateId.PreGenesis : Block(number - 1), Block(number));

    /// <summary>What <see cref="Reads"/> finds at the state of block <paramref name="number"/>.</summary>
    public static List<string> ExpectedReads(ulong number) =>
    [
        $"account {BlockAddress} {number} {number} {Keccak.EmptyTreeHash} {Keccak.OfAnEmptyString}",
        $"slot {BlockAddress} {Slot} {number + 1}",
        $"state node {NodePath} {Rlp(number).ToHexString()}",
        $"filtered slot {BlockAddress} {Slot} {number + 1}",
    ];

    public static List<string> Reads(FlatManagerContainer container, in StateId state) =>
        BundleReads.Read(container.Arm, state, [BlockAddress], [Slot], [NodePath], []);

    /// <summary>The blocks of the chain the container holds a state of.</summary>
    public static ulong[] Held(FlatManagerContainer container, ulong last) =>
        [.. Enumerable.Range(0, (int)last + 1).Select(static number => (ulong)number).Where(number => container.Arm.HasStateForBlock(Block(number)))];

    /// <summary>The catalog rows of the tier in <paramref name="basePath"/>, read once no manager holds the catalog.</summary>
    public static CatalogEntry[] Catalog(string basePath)
    {
        using FlatTestDbs.TrackingCatalogDb catalogDb = FlatTestDbs.OpenRocksDbCatalog(basePath);
        return [.. new SnapshotCatalog(catalogDb).Load()];
    }

    /// <summary>The file of the arena <paramref name="arenaId"/> in <paramref name="basePath"/>, whichever kind of arena it is.</summary>
    public static string ArenaFile(string basePath, int arenaId) =>
        Directory.EnumerateFiles(ArenaDirectory(basePath)).Single(file => file.EndsWith($"_{arenaId:D4}.bin"));

    /// <summary>The ids of the arena files in <paramref name="basePath"/>.</summary>
    public static int[] ArenaIds(string basePath) =>
        [.. Directory.EnumerateFiles(ArenaDirectory(basePath)).Select(static file => int.Parse(Path.GetFileNameWithoutExtension(file).Split('_')[^1])).Order()];

    public static string ArenaDirectory(string basePath) => Path.Combine(basePath, "persistedSnapshot", "arena");

    public static string BlobDirectory(string basePath) => Path.Combine(basePath, "persistedSnapshot", "blob");

    /// <summary>A 40-byte RLP list of <paramref name="number"/>, long enough to be stored by hash.</summary>
    public static byte[] Rlp(ulong number) => [0xe7, .. Enumerable.Range(0, 39).Select(i => (byte)(number + (ulong)i))];
}
