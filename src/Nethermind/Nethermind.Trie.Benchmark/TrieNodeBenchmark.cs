// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.Benchmark
{
    [MemoryDiagnoser]
    [DryJob]
    public class TrieNodeBenchmark
    {
        // public readonly struct Param
        // {
        //     public Param(byte[] bytes)
        //     {
        //         Bytes = bytes;
        //     }
        //     
        //     public byte[] Bytes { get; }
        //
        //     public override string ToString()
        //     {
        //         return $"bytes[{Bytes.Length.ToString().PadLeft(4, '0')}]";
        //     }
        // }
        //
        // public IEnumerable<Param> Inputs 
        // {
        //     get
        //     {
        //         yield return new Param(new byte[0]);
        //         yield return new Param(new byte[32]);
        //         yield return new Param(new byte[64]);
        //         yield return new Param(new byte[96]);
        //         yield return new Param(new byte[128]);
        //         yield return new Param(new byte[1024]);
        //         yield return new Param(new byte[2048]);
        //     }
        // }
        //
        // [ParamsSource(nameof(Inputs))]
        // public Param Input { get; set; }

        [Benchmark]
        public TrieNode Just_trie_node() => new(NodeType.Unknown);

        [Benchmark]
        public Hash256 Just_keccak() => Keccak.Compute(_bytes);

        private byte[] _bytes = new byte[32];

        private long _i = 0;

        [Benchmark]
        public TrieNode Just_trie_node_with_hash()
        {
            BinaryPrimitives.WriteInt64BigEndian(_bytes, _i);
            TrieNode trieNode = new(NodeType.Unknown, Keccak.Compute(_bytes));
            return trieNode;
        }

        [Benchmark]
        public TrieNode Just_trie_node_with_rlp() => new(NodeType.Unknown, new byte[7]);

        [Benchmark]
        public TrieNode Just_extension_with_child()
        {
            TrieNode trieNode = new(NodeType.Extension);
            trieNode.SetChild(0, null);
            return trieNode;
        }

        [Benchmark]
        public TrieNode Just_branch_with_child()
        {
            TrieNode trieNode = new(NodeType.Branch);
            trieNode.SetChild(0, null);
            return trieNode;
        }

        [Benchmark]
        public TrieNode Just_leaf_with_value()
        {
            TrieNode trieNode = new(NodeType.Leaf);
            trieNode.Value = new byte[7];
            return trieNode;
        }

        [Benchmark]
        public byte[] Just_hex_prefix() => HexPrefix.ToBytes(new byte[5], true);

        [Benchmark]
        public Rlp Just_rlp() => new(new byte[8]);

        [Benchmark]
        public Rlp Just_rlp_aligned() => new(new byte[1]);

    }
}
