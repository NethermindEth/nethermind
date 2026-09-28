// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.State.Flat;
using Nethermind.Trie;
using FlatSnapshot = Nethermind.State.Flat.Snapshot;

namespace Nethermind.Benchmarks.State;

/// <summary>
/// Address derivation shared by the flat-state benchmarks in this folder.
/// </summary>
internal static class FlatWorldStateBenchmarkHarness
{
    internal static Address DeriveAddress(int index) =>
        new(Keccak.Compute(Address.FromNumber((UInt256)(ulong)index).Bytes));

    /// <summary>
    /// Fails the run while tiered compilation is still enabled, instead of letting the benchmark
    /// report Tier0 timings as if they were steady-state ones.
    /// </summary>
    /// <remarks>
    /// <see cref="NoTieredCompilationAttribute"/> only reaches a benchmark that runs in a child
    /// process. An in-process toolchain cannot change the environment of a runtime that is already
    /// up, yet BenchmarkDotNet still prints the variable in the job header, so without this check an
    /// <c>--inProcess</c> run quietly measures whichever tier it happened to reach.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Tiered compilation is enabled.</exception>
    internal static void RequireTieredCompilationDisabled()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") is not "0"
            && Environment.GetEnvironmentVariable("COMPlus_TieredCompilation") is not "0")
        {
            throw new InvalidOperationException(
                "Set DOTNET_TieredCompilation=0 in the environment before running this benchmark; an in-process run cannot set it itself.");
        }
    }
}

/// <summary>
/// Job mutator (applies on top of whatever job the runner selects, e.g. via <c>--job</c>) that
/// disables tiered compilation. Tiered compilation's mid-run re-JIT (and OSR patching of a hot
/// loop) can land inside one of the handful of measured iterations these benchmarks run, showing
/// up as a variance spike unrelated to the code under measurement.
/// </summary>
/// <remarks>
/// This has no effect on an in-process run;
/// <see cref="FlatWorldStateBenchmarkHarness.RequireTieredCompilationDisabled"/> is what makes that
/// case fail rather than report Tier0 timings.
/// </remarks>
internal sealed class NoTieredCompilationAttribute : JobMutatorConfigBaseAttribute
{
    public NoTieredCompilationAttribute()
        : base(Job.Default.WithEnvironmentVariable("DOTNET_TieredCompilation", "0"))
    {
    }
}

/// <summary>Trie node cache that never caches — these benchmarks don't measure caching.</summary>
internal sealed class NullTrieNodeCache : ITrieNodeCache
{
    public bool TryGet(Hash256 address, in TreePath path, Hash256 hash, out TrieNode node)
    {
        node = null;
        return false;
    }

    public void Add(TransientResource transientResource) { }

    public void Clear() { }
}

/// <summary>Commit target that only captures the snapshot a <see cref="FlatWorldStateScope"/> commit produces.</summary>
internal sealed class CapturingCommitTarget : IFlatCommitTarget
{
    public FlatSnapshot LastSnapshot { get; private set; }
    public TransientResource LastResource { get; private set; }

    public void AddSnapshot(FlatSnapshot snapshot, TransientResource transientResource)
    {
        LastSnapshot = snapshot;
        LastResource = transientResource;
    }
}

/// <summary>Code DB stub — none of these benchmarks exercise contract code storage.</summary>
internal sealed class NullCodeDb : IWorldStateScopeProvider.ICodeDb
{
    public byte[] GetCode(in ValueHash256 codeHash) => null;

    public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite()
        => NullCodeSetter.Instance;

    private sealed class NullCodeSetter : IWorldStateScopeProvider.ICodeSetter
    {
        public static readonly NullCodeSetter Instance = new();

        public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) { }

        public void Dispose() { }
    }
}
