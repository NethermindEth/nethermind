using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

namespace Nethermind.Trie.Benchmark
{
    [MemoryDiagnoser]
    [DryJob(RuntimeMoniker.NetCoreApp31)]
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
        public TrieNode Just_trie_node_56B()
        {
            return new TrieNode(NodeType.Unknown);
        }

        [Benchmark]
        public Keccak Just_keccak_80B()
        {
            return Keccak.Compute(_bytes);
        }

        private byte[] _bytes = new byte[32];

        private long _i = 0;

        [Benchmark]
        public TrieNode Just_trie_node_with_hash_136B()
        {
            BinaryPrimitives.WriteInt64BigEndian(_bytes, _i);
            TrieNode trieNode = new TrieNode(NodeType.Unknown, Keccak.Compute(_bytes));
            return trieNode;
        }

        [Benchmark]
        public TrieNode Just_trie_node_with_rlp_120B()
        {
            return new TrieNode(NodeType.Unknown, new byte[7]);
        }
        
        [Benchmark]
        public TrieNode Just_extension_with_child_96B()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, null);
            return trieNode;
        }

        [Benchmark]
        public TrieNode Just_branch_with_child_208B()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(0, null);
            return trieNode;
        }

        [Benchmark]
        public TrieNode Just_leaf_with_value_128B()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf);
            trieNode.Value = new byte[7];
            return trieNode;
        }

        [Benchmark]
        public HexPrefix Just_hex_prefix_64B()
        {
            return new HexPrefix(true, new byte[5]);
        }

        [Benchmark]
        public Rlp Just_rlp_56B()
        {
            return new Rlp(new byte[8]);
        }
        
        [Benchmark]
        public Rlp Just_rlp_aligned_56B()
        {
            return new Rlp(new byte[1]);
        }

        [Benchmark]
        public RlpStream Just_rlp_stream_64B()
        {
            return new RlpStream(new byte[7]);
        }
        
        [Benchmark]
        public RlpStream Just_rlp_stream_160B()
        {
            return new RlpStream(new byte[100]);
        }
    }
}