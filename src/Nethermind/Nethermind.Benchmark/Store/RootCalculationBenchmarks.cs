// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Single-threaded root calculation over identical prepared parents and updates, the core
/// performance gate for trie root-calculation implementations. The Patricia arms are the
/// reference baseline; comparison arms read the same <see cref="TrieRootFixture"/> inputs.
/// </summary>
/// <remarks>
/// The gate compares a sparse calculate-and-stage arm against <see cref="PatriciaCalculate"/>
/// (the cheaper Patricia arm, which computes the root without emitting persistable nodes), so
/// the required improvement is measured conservatively in Patricia's favor;
/// <see cref="PatriciaCalculateAndStage"/> is the like-for-like context row that adds Patricia's
/// commit traversal through a non-writing committer. The root equality check inside each arm is
/// a single 32-byte compare, identical across arms.
/// </remarks>
[MemoryDiagnoser]
public class RootCalculationBenchmarks
{
    [Params("storage-tiny", "storage-realblocks", "state-realblocks", "storage-dominant", "state-superblock")]
    public string Fixture { get; set; } = null!;

    private TrieRootFixture _fixture = null!;

    [GlobalSetup]
    public void Setup() => _fixture = TrieRootFixture.CreateGateFixture(Fixture);

    [Benchmark(Baseline = true)]
    public Hash256 PatriciaCalculate()
    {
        PatriciaTree tree = new(new RawScopedTrieStore(_fixture.ParentStorage), _fixture.ParentRoot, true, NullLogManager.Instance);

        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(_fixture.Updates))
        {
            tree.BulkSet(in entries, PatriciaTree.Flags.DoNotParallelize);
        }

        tree.UpdateRootHash(canBeParallel: false);
        return Verify(tree.RootHash);
    }

    [Benchmark]
    public Hash256 PatriciaCalculateAndStage()
    {
        TrieRootFixture.RecordingTrieStore store = new(new RawScopedTrieStore(_fixture.ParentStorage), collectNodes: false);
        PatriciaTree tree = new(store, _fixture.ParentRoot, true, NullLogManager.Instance);

        using (ArrayPoolListRef<PatriciaTree.BulkSetEntry> entries = new(_fixture.Updates))
        {
            tree.BulkSet(in entries, PatriciaTree.Flags.DoNotParallelize);
        }

        tree.Commit();
        return Verify(tree.RootHash);
    }

    private Hash256 Verify(Hash256 root) =>
        root == _fixture.ExpectedRoot ? root : throw new InvalidOperationException($"Root mismatch for {Fixture}: {root} != {_fixture.ExpectedRoot}");
}
