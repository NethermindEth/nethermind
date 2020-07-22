using System.Buffers.Binary;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

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

        [Benchmark] // 56B
        public TrieNode Just_trie_node()
        {
            return new TrieNode(NodeType.Unknown);
        }
        
        [Benchmark] // 56B
        public Keccak Just_keccak()
        {
            return Keccak.Compute(_bytes);
        }
        
        private byte[] _bytes = new byte[32];

        private long _i = 0;
        
        [Benchmark] // 136B
        public TrieNode Just_trie_node_with_hash()
        {
            TrieNode trieNode = new TrieNode(NodeType.Unknown);
            BinaryPrimitives.WriteInt64BigEndian(_bytes, _i);
            trieNode.Keccak = Keccak.Compute(_bytes);
            return trieNode;
        }
        
        [Benchmark]
        public TrieNode Just_trie_node_with_rlp()
        {
            return new TrieNode(NodeType.Unknown, new byte[7]);
        }
        
        [Benchmark] // 60B
        public RlpStream Just_rlp_stream()
        {
            return new RlpStream(new byte[7]);
        }
        
        [Benchmark] // 96B
        public TrieNode Just_extension_with_child()
        {
            TrieNode trieNode = new TrieNode(NodeType.Extension);
            trieNode.SetChild(0, null);
            return trieNode;
        }
        
        [Benchmark] // 208B
        public TrieNode Just_branch_with_child()
        {
            TrieNode trieNode = new TrieNode(NodeType.Branch);
            trieNode.SetChild(0, null);
            return trieNode;
        }
        
        [Benchmark] // 208B
        public TrieNode Just_leaf_with_value()
        {
            TrieNode trieNode = new TrieNode(NodeType.Leaf);
            trieNode.Value = new byte[7];
            return trieNode;
        }
    }
}