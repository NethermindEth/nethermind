// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using FluentAssertions;
using Nethermind.Core.Buffers;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.Benchmarks.Rlp;

[MemoryDiagnoser]
public class RlpTrieNodeEncodingBenchmark
{
    private readonly TrieNode _extension;
    private readonly TrieNode _branch;
    private readonly TrieNode _leaf;
    private readonly RawScopedTrieStore _store;

    public RlpTrieNodeEncodingBenchmark()
    {
        _store = new RawScopedTrieStore(new NodeStorage(new MemDb()), null);
        var tree = new PatriciaTree(_store, NullLogManager.Instance);

        // Some simple nodes to create E->B->L1, ...
        tree.Set([0b0000_0000], [1, 2, 3, 4, 5, 6, 7, 1]);
        tree.Set([0b0000_0001], [1, 2, 3, 4, 5, 6, 7, 2]);
        tree.Set([0b0000_0010], [1, 2, 3, 4, 5, 6, 7, 3]);
        tree.Set([0b0000_0011], [1, 2, 3, 4, 5, 6, 7, 4]);
        tree.Set([0b0000_0100], [1, 2, 3, 4, 5, 6, 7, 5]);
        tree.Set([0b0000_0101], [1, 2, 3, 4, 5, 6, 7, 6]);
        tree.Set([0b0000_0110], [1, 2, 3, 4, 5, 6, 7, 7]);
        tree.Set([0b0000_0111], [1, 2, 3, 4, 5, 6, 7, 8]);

        tree.Commit();

        var extension = tree.Root;

        _extension = extension;
        _extension.NodeType.Should().Be(NodeType.Extension);

        TreePath path = default;

        _branch = _extension.GetChild(_store, ref path, 0);

        path.AppendMut(0);
        _branch.TryResolveNode(_store, ref path);
        _branch.NodeType.Should().Be(NodeType.Branch);

        _leaf = _branch.GetChild(_store, ref path, 0);
        _leaf.TryResolveNode(_store, ref path);
        _leaf.NodeType.Should().Be(NodeType.Leaf);
    }

    [Benchmark]
    public CappedArray<byte> Encode_Extension()
    {
        TreePath path = default;
        return _extension.RlpEncode(_store, ref path);
    }

    [Benchmark]
    public CappedArray<byte> Encode_Branch()
    {
        TreePath path = default;
        return _branch.RlpEncode(_store, ref path);
    }

    [Benchmark]
    public CappedArray<byte> Encode_Leaf()
    {
        TreePath path = default;
        return _leaf.RlpEncode(_store, ref path);
    }
}
