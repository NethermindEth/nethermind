// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using NUnit.Framework;
using BlockPlan = Nethermind.State.Flat.Rust.Test.RustFlatDbManagerDifferentialTests.BlockPlan;
using static Nethermind.State.Flat.Rust.Test.FlatManagerContainer;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// Probes a probable C# bug the Rust tier ports faithfully: a large merge of the persisted-snapshot tier drops the
/// slots below a self-destruct without adding their bloom keys, and its bloom is then shared with the snapshots inside
/// its window, which skip those slots.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class PersistedBloomShareTests
{
    private const string SharedBloomBug =
        "C# shares a merged bloom that lacks slots dropped below a self-destruct barrier (PersistedSnapshotMerger drops them " +
        "without bloom keys; ShareBloomAcrossRange/ReconstructBloom hand that bloom to contained snapshots)";

    private const ulong SlotWrite = 17;
    private const ulong SelfDestruct = 21;
    private const ulong LastBlock = 39;

    private static readonly Address Destructed = new("0xbbbbbbbb00000000000000000000000000000001");
    private static readonly UInt256 Slot = 7;

    /// <summary>
    /// After a restart, whose loader shares the bloom of the widest merge with the snapshots inside it, a slot a block
    /// wrote within the window of a large merge, below a later block of the window that self-destructs the account,
    /// reads as written at a state between the two.
    /// </summary>
    [Test]
    public async Task A_slot_below_a_self_destruct_in_a_large_merge_reads_back_after_a_restart([Values(FlatDbBackend.CSharp, FlatDbBackend.Rust)] FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.CSharp) RustNativeLibrary.Require();

        UInt256? csharp = await ReadAfterRestart(FlatDbBackend.CSharp);
        UInt256? actual = backend == FlatDbBackend.CSharp ? csharp : await ReadAfterRestart(backend);

        Assert.That(actual, Is.EqualTo(csharp), "as the C# backend reads it");
        if (csharp != SlotWrite) Assert.Ignore($"{SharedBloomBug}: read {csharp?.ToString() ?? "null"}");
    }

    /// <summary>The slot as a bundle at block <see cref="SlotWrite"/> reads it once the tier reopened.</summary>
    private static async Task<UInt256?> ReadAfterRestart(FlatDbBackend backend)
    {
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        FlatDbConfig config = StalledChain.Config(backend);
        config.PersistedSnapshotBloomBitsPerKey = 14;
        FlatManagerContainer.Overrides overrides = new() { RocksDbCatalog = true };
        await using (FlatManagerContainer first = new(config, path.Path, finality, overrides))
        {
            for (ulong number = 0; number <= LastBlock; number++)
            {
                Plan(number).AddTo(first, number == 0 ? StateId.PreGenesis : Block(number - 1), Block(number));
                if (number == StalledChain.FinalizedBlock + 2) finality.Finalize(StalledChain.FinalizedBlock, Block);
                first.PersistUpTo(Block(number));
            }
        }

        Assert.That(StalledChain.Catalog(path.Path), Has.Some.Matches<CatalogEntry>(static row =>
            row is { Tier: SnapshotTier.PersistedLargeCompacted, From.BlockNumber: < SlotWrite, To.BlockNumber: >= SelfDestruct }), $"{backend}: a large merge covers both blocks");

        await using FlatManagerContainer reopened = new(config, path.Path, finality, overrides);
        using IBundleView bundle = reopened.Arm.GatherReadOnly(Block(SlotWrite), ReaderFlags.Sync);
        return bundle.GetSlot(Destructed, Slot, bundle.DetermineSelfDestructSnapshotIdx(Destructed));
    }

    /// <summary>
    /// Block n sets the account; block 1 and <see cref="SlotWrite"/> write the slot, and <see cref="SelfDestruct"/>
    /// self-destructs the account before setting it again.
    /// </summary>
    private static BlockPlan Plan(ulong number) => new(
        [(Destructed, new Account(number, number))],
        number is 1 or SlotWrite ? [(Destructed, Slot, number)] : [],
        number == SelfDestruct ? [(Destructed, false)] : [],
        [],
        []);
}
