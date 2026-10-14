// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
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
/// flush or without one, which leaves the blocks above the persisted state to be processed again; the DB then reopens
/// on the other backend, which must find the same persisted state, read the same through it and go on processing
/// blocks, every observation and the columns after reopening and at the end matching a run on the C# backend alone.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class CrossBackendRestartTests
{
    private const ulong FirstRunBlocks = 20;
    private const ulong SecondRunBlocks = 12;

    /// <summary>Finality trails the head by this much, so a shutdown without a flush leaves blocks above the persisted state.</summary>
    private const ulong FinalityLag = 6;

    private static readonly FlatLayout[] Layouts = GoldenVectorGenerator.Layouts;
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
        [Values] bool flushBeforeShutdown)
    {
        if (writer != FlatDbBackend.CSharp || reader != FlatDbBackend.CSharp) RustNativeLibrary.Require();

        List<string> expected = await Run(FlatDbBackend.CSharp, FlatDbBackend.CSharp, layout, flushBeforeShutdown);
        List<string> actual = await Run(writer, reader, layout, flushBeforeShutdown);

        Assert.That(actual, Is.EqualTo(expected));
        Assert.That(expected, Does.Contain($"head retained after reopen {flushBeforeShutdown}"), "only a flush persists the head");
    }

    /// <summary>The persisted states, the states retained, the reads and the columns of a run.</summary>
    private static async Task<List<string>> Run(FlatDbBackend writer, FlatDbBackend reader, FlatLayout layout, bool flushBeforeShutdown)
    {
        using TempPath path = TempPath.GetTempDirectory();
        TestFinality finality = new();
        List<string> observed = [];

        await using (FlatManagerContainer first = new(FlatManagerContainer.Config(writer, layout, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), path.Path, finality))
        {
            for (ulong number = 0; number < FirstRunBlocks; number++) Add(first, finality, number);
            if (flushBeforeShutdown) first.Arm.FlushCache(CancellationToken.None);
            observed.Add($"persisted at shutdown {first.PersistenceManager.GetCurrentPersistedStateId()}");
        }

        await using FlatManagerContainer second = new(FlatManagerContainer.Config(reader, layout, carryForward: false, maxInMemorySnapshotBytes: 0, longFinality: false), path.Path, finality);
        StateId persisted = second.PersistenceManager.GetCurrentPersistedStateId();
        observed.Add($"persisted after reopen {persisted}");
        observed.Add($"head retained after reopen {second.Arm.HasStateForBlock(Block(FirstRunBlocks - 1))}");
        observed.AddRange(Columns(second, "after reopen"));
        observed.AddRange(Reads(second, persisted));

        // The blocks above the persisted state are processed again, as the node does after a shutdown without a flush.
        for (ulong number = persisted.BlockNumber + 1; number < FirstRunBlocks + SecondRunBlocks; number++) Add(second, finality, number);
        observed.AddRange(Reads(second, Block(FirstRunBlocks + SecondRunBlocks - 1)));
        second.Arm.FlushCache(CancellationToken.None);
        observed.Add($"persisted at the end {second.PersistenceManager.GetCurrentPersistedStateId()}");
        observed.AddRange(Columns(second, "at the end"));
        return observed;
    }

    /// <summary>Adds block <paramref name="number"/>, finalizes the block <see cref="FinalityLag"/> below it and persists what that allows.</summary>
    private static void Add(FlatManagerContainer container, TestFinality finality, ulong number)
    {
        Plan(number).AddTo(container, number == 0 ? StateId.PreGenesis : Block(number - 1), Block(number));
        if (number > FinalityLag + finality.FinalizedBlockNumber) finality.Finalize(number - FinalityLag, Block);
        container.PersistUpTo(Block(number));
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

    private static byte[] Rlp(ulong number, int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(number + (ulong)i))];

    private static IEnumerable<string> Reads(FlatManagerContainer container, StateId state) =>
        BundleReads.Read(container.Arm, state, Addresses, Slots, Paths, Owners).Select(read => $"at {state}: {read}");

    private static IEnumerable<string> Columns(FlatManagerContainer container, string when) =>
        FlatTestDbs.DumpAll(container.Db).Select(entry => $"{when}: {entry}");
}
