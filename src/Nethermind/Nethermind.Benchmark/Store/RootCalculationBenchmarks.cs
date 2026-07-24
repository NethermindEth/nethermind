// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Nethermind.Trie.Sparse;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Root calculation over identical prepared parents and updates, the core performance gate for
/// trie root-calculation implementations. The Patricia arms are the reference baseline;
/// comparison arms read the same <see cref="TrieRootFixture"/> inputs.
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
    private SparseTrieUpdate[] _sparsePristine = null!;
    private SparseTrieUpdate[] _sparseScratch = null!;

    [GlobalSetup]
    public void Setup()
    {
        _fixture = TrieRootFixture.CreateGateFixture(Fixture);
        _sparsePristine = new SparseTrieUpdate[_fixture.Updates.Length];
        for (int i = 0; i < _sparsePristine.Length; i++)
        {
            PatriciaTree.BulkSetEntry entry = _fixture.Updates[i];
            _sparsePristine[i] = new SparseTrieUpdate(entry.Path, entry.Value.Length == 0 ? null : entry.Value);
        }

        _sparseScratch = new SparseTrieUpdate[_sparsePristine.Length];
    }

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

    /// <summary>
    /// The gate arm: sparse reveal/apply plus fused encode/hash that also stages every
    /// persistable RLP in its final owned array during calculation. Publication (draining staged
    /// records into snapshot destinations) is measured separately, but unlike
    /// <see cref="PatriciaCalculate"/> the persistable output already exists when this returns.
    /// </summary>
    [Benchmark]
    public ValueHash256 SparseCalculateAndStage() => SparseCalculateAndStage(canBeParallel: false);

    [Benchmark]
    public ValueHash256 SparseCalculateAndStageParallelRoot() => SparseCalculateAndStage(canBeParallel: true);

    private ValueHash256 SparseCalculateAndStage(bool canBeParallel)
    {
        _sparsePristine.CopyTo(_sparseScratch, 0);
        NodeStorageSparseSource source = new(_fixture.ParentStorage);
        using SparseTrie sparse = new(source, _fixture.ParentRoot.ValueHash256, nodeCapacityHint: _sparseScratch.Length * 4);
        sparse.Apply(_sparseScratch);
        ValueHash256 root = sparse.CalculateRoot(canBeParallel);
        if (root != _fixture.ExpectedRoot.ValueHash256)
        {
            ThrowRootMismatch(Fixture, root, _fixture.ExpectedRoot);
        }

        return root;
    }

    private Hash256 Verify(Hash256 root)
    {
        if (root != _fixture.ExpectedRoot)
        {
            ThrowRootMismatch(Fixture, root.ValueHash256, _fixture.ExpectedRoot);
        }

        return root;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowRootMismatch(string fixture, in ValueHash256 root, Hash256 expectedRoot) =>
        throw new InvalidOperationException($"Root mismatch for {fixture}: {root} != {expectedRoot}");

    // NodeStorage keys entries by (path, hash), so a returned value is the requested node by
    // construction and re-validating its keccak here would only measure hashing the parents a
    // second time - a cost the Patricia arms never pay. The Flat reader validates where it must:
    // on its path-keyed persistence tier, where the check rides on the database read.
    private sealed class NodeStorageSparseSource(NodeStorage storage) : ISparseTrieNodeSource
    {
        public void Resolve(ReadOnlySpan<SparseNodeRequest> requests, Span<CappedArray<byte>> results)
        {
            for (int i = 0; i < requests.Length; i++)
            {
                byte[] rlp = storage.Get(null, requests[i].Path, requests[i].Hash.ToCommitment());
                results[i] = rlp is null ? CappedArray<byte>.Null : rlp;
            }
        }
    }
}
