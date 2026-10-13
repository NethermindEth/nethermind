// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>
/// The overridable world scope of the forked package against the original's, over a real node of each backend: its
/// commits stay its own, chain into the snapshots a later scope is seeded with, a storage cleared mid-chain hides the
/// slots below it, a scope opened before <see cref="IOverridableWorldScope.ResetOverrides"/> keeps reading, and on the
/// forked package every native content the scope made is released once the scopes are gone.
/// </summary>
[Parallelizable(ParallelScope.All)]
public class ForkOverridableWorldScopeTests
{
    private static readonly Address A = new("0xffffffff00000000000000000000000000000001");
    private static readonly Address B = new("0xffffffff00000000000000000000000000000002");
    private static readonly UInt256[] Slots = [1, 2, 3];

    [Test]
    public async Task Overridable_scope_reads_and_releases_its_own_commits_as_the_original()
    {
        RustNativeLibrary.Require();
        (List<string> original, _) = await Run(FlatDbBackend.CSharp);
        (List<string> fork, long[] liveContents) = await Run(FlatDbBackend.Rust);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fork, Is.EqualTo(original));
            Assert.That(liveContents[1], Is.GreaterThan(liveContents[0]), "the scope's commits hold native contents");
            Assert.That(liveContents[2], Is.EqualTo(liveContents[0]), "every native content of the scope is released");
        }
    }

    /// <returns>What the run read, and on the Rust backend the live native contents of read-only scopes before, during and after it.</returns>
    private static async Task<(List<string> Reads, long[] LiveContents)> Run(FlatDbBackend backend)
    {
        await using WorldStateNode node = new(backend);
        List<string> reads = [];
        List<long> liveContents = [LiveReadOnlyContents(node, backend)];
        IWorldStateManager manager = node.WorldStateManager;
        IOverridableWorldScope overridable = manager.CreateOverridableWorldScope();

        BlockHeader first = WorldStateNode.Commit(overridable.WorldState, null, 0, WorldStateNode.Write(A, new Account(1, 10), false, (1, 11), (2, 12)));
        BlockHeader second = WorldStateNode.Commit(overridable.WorldState, first, 1, WorldStateNode.Write(A, new Account(2, 20), true, (3, 33)));
        BlockHeader third = WorldStateNode.Commit(overridable.WorldState, second, 2, WorldStateNode.Write(B, new Account(3, 30), false, (1, 41)));
        foreach (BlockHeader header in new[] { first, second, third })
        {
            reads.AddRange(WorldStateNode.Reads(overridable.GlobalStateReader, header, [A, B], Slots));
        }

        reads.Add($"main manager holds the overridable state {manager.GlobalWorldState.HasRoot(third)}");
        Assert.That(overridable.WorldState.TryBeginScope(third, new LocalMetrics(), out IWorldStateScopeProvider.IScope? live), Is.True);
        liveContents.Add(LiveReadOnlyContents(node, backend));
        overridable.ResetOverrides();
        reads.Add($"after the reset the overridable scope holds the state {overridable.WorldState.HasRoot(third)}");
        reads.Add($"the scope opened before the reset reads {live!.Get(A)?.Balance} {Slot(live, A, 1)} {Slot(live, A, 3)} {live.Get(B)?.Balance}");
        live.Dispose();
        overridable.Dispose();
        liveContents.Add(LiveReadOnlyContents(node, backend));
        return (reads, [.. liveContents]);
    }

    private static UInt256 Slot(IWorldStateScopeProvider.IScope scope, Address address, UInt256 slot)
    {
        scope.CreateStorageTree(address).Get(slot, out UInt256 value);
        return value;
    }

    private static unsafe long LiveReadOnlyContents(WorldStateNode node, FlatDbBackend backend)
    {
        if (backend != FlatDbBackend.Rust) return 0;

        ManagerHandle manager = node.Resolve<Fork.NativeFlatManager>().Handle;
        FdbCounters* counters = NativeMethods.fdb_stats(manager.DangerousGetHandle());
        return Volatile.Read(ref counters->ContentActive[(int)ResourcePool.Usage.ReadOnlyProcessingEnv]);
    }
}
