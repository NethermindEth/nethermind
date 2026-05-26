// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.Benchmark;

[MemoryDiagnoser]
public class TrieNodeBenchmark
{
    private readonly byte[] _bytes = new byte[32];

    [Benchmark]
    public TrieNode Just_trie_node_56B() => new(NodeType.Unknown);

    [Benchmark]
    public Hash256 Just_keccak() => Keccak.Compute(_bytes);

    [Benchmark]
    public TrieNode Just_trie_node_with_hash()
    {
        BinaryPrimitives.WriteInt64BigEndian(_bytes, 42);
        TrieNode trieNode = new(NodeType.Unknown, Keccak.Compute(_bytes));
        return trieNode;
    }

    [Benchmark]
    public TrieNode Just_trie_node_with_rlp() => new(NodeType.Unknown, new byte[7]);

    [Benchmark]
    public RlpStream Just_rlp_stream() => new(new byte[7]);
}
