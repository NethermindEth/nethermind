// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Autofac;
using Nethermind.Db;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Rust.Interop;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The persisted-snapshot tier of a native manager, read without applying its persisted-node events or publishing its metrics.</summary>
internal static unsafe class NativeTier
{
    /// <summary>The persisted snapshots of each tier and size with a non-zero gauge.</summary>
    public static FfiTierGaugeRow[] Gauges(Fork.NativeFlatManager manager)
    {
        const int capacity = 64;
        FfiTierGaugeRow* rows = stackalloc FfiTierGaugeRow[capacity];
        nuint count;
        bool added = false;
        try
        {
            manager.Handle.DangerousAddRef(ref added);
            StatusMap.ThrowIfFailed(NativeMethods.fdb_persisted_tier_gauges(manager.Handle.DangerousGetHandle(), rows, capacity, &count));
        }
        finally
        {
            if (added) manager.Handle.DangerousRelease();
        }

        return new ReadOnlySpan<FfiTierGaugeRow>(rows, (int)count).ToArray();
    }

    /// <summary>The persisted snapshots of <paramref name="tier"/>.</summary>
    public static long Snapshots(Fork.NativeFlatManager manager, SnapshotTier tier) =>
        Gauges(manager).Where(row => (SnapshotTier)row.Tier == tier).Sum(static row => row.Count);

    /// <summary>The persisted snapshots of every tier.</summary>
    public static long Snapshots(Fork.NativeFlatManager manager) => Gauges(manager).Sum(static row => row.Count);

    /// <summary>The snapshots in the persisted-snapshot tier of the flat DB <paramref name="context"/> runs, on either backend.</summary>
    public static long PersistedSnapshotCount(IComponentContext context) =>
        context.Resolve<IFlatDbConfig>().Backend == FlatDbBackend.Rust
            ? Snapshots(context.Resolve<Fork.NativeFlatManager>())
            : context.Resolve<ISnapshotRepository>().PersistedSnapshotCount;

    /// <summary>What the library reports when it gathers a bundle at <paramref name="state"/>, past its bundle cache.</summary>
    public static FfiRobInfo GatherInfo(Fork.NativeFlatManager manager, in StateId state)
    {
        FfiStateId nativeBase = new(state);
        nint bundle;
        FfiRobInfo info;
        StatusMap.ThrowIfFailed(NativeMethods.fdb_rob_gather(manager.Handle, &nativeBase, (uint)ReaderFlags.Sync, &bundle, &info));
        StatusMap.ThrowIfFailed(NativeMethods.fdb_rob_release(bundle));
        return info;
    }

    /// <summary>
    /// Waits until the persisted-snapshot tiers whose <see cref="Fingerprint"/>s <paramref name="fingerprints"/> takes
    /// stop changing for 100 ms, as the persisted-snapshot compactors of their backends drain; fails when they have not
    /// within 10 s.
    /// </summary>
    /// <remarks>
    /// A merged range keeps the storage trie nodes its blocks wrote before a self-destruct, and a flush writes them
    /// after the self-destruct's delete, while a flush of the same blocks one by one deletes them; so what a flush
    /// writes depends on whether the compactor merged the range yet (on both backends, as the C# one does it). A merge
    /// shows only once it publishes its snapshot, as a new snapshot or as the twins sharing its bloom, so one longer
    /// than the quiet period can still be running when this returns.
    /// </remarks>
    public static void WaitUntilSettled(params Func<long>[] fingerprints)
    {
        long[] last = [.. fingerprints.Select(static fingerprint => fingerprint())];
        long deadline = Environment.TickCount64 + 10_000;
        for (int unchanged = 0; unchanged < 4;)
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("The persisted-snapshot tiers kept changing for 10 s");
            Thread.Sleep(25);
            long[] now = [.. fingerprints.Select(static fingerprint => fingerprint())];
            unchanged = now.SequenceEqual(last) ? unchanged + 1 : 0;
            last = now;
        }
    }

    /// <summary>
    /// Changes whenever the persisted-snapshot compactor of the flat DB <paramref name="context"/> runs publishes a merge
    /// or a prune drops a snapshot of its tier.
    /// </summary>
    public static long Fingerprint(IComponentContext context)
    {
        if (context.Resolve<IFlatDbConfig>().Backend != FlatDbBackend.Rust)
        {
            return context.Resolve<ISnapshotRepository>().PersistedSnapshots.Sum(static snapshot => (long)RuntimeHelpers.GetHashCode(snapshot));
        }

        Fork.NativeFlatManager manager = context.Resolve<Fork.NativeFlatManager>();
        FdbCounters counters = Counters(manager);
        return HashCode.Combine(Snapshots(manager), counters.PersistedSnapshotCompactions, counters.PersistedSnapshotPrunes);
    }

    /// <summary>The counters of the native manager as they are now.</summary>
    public static FdbCounters Counters(Fork.NativeFlatManager manager)
    {
        bool added = false;
        try
        {
            manager.Handle.DangerousAddRef(ref added);
            return *NativeMethods.fdb_stats(manager.Handle.DangerousGetHandle());
        }
        finally
        {
            if (added) manager.Handle.DangerousRelease();
        }
    }
}
