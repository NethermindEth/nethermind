using System.Security.Cryptography;
using System;
using System.Runtime.CompilerServices;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;

[assembly:InternalsVisibleTo("Nethermind.Baseline.Test")]
namespace Nethermind.Baseline
{
    public class MerkleTree
    {
        private readonly IKeyValueStore _keyValueStore;

        private static readonly byte[][] s_zeroHashes = new byte[32][];
        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();
        private readonly byte[] _countKey = new byte[] {0};

        static MerkleTree()
        {
            s_zeroHashes[0] = new byte[32];
            for (int index = 1; index < 32; index++)
            {
                s_zeroHashes[index] = Hash(s_zeroHashes[index - 1], s_zeroHashes[index - 1]);
            }
        }

        public int Count { get; set; }

        public MerkleTree(IKeyValueStore keyValueStore)
        {
            _keyValueStore = keyValueStore ?? throw new ArgumentNullException(nameof(keyValueStore));

            byte[] countBytes = _keyValueStore[_countKey];
            Count = countBytes == null ? 0 : new RlpStream(countBytes).DecodeInt();
        }
        
        private void StoreCountInTheDb()
        {
            _keyValueStore[_countKey] = Rlp.Encode(Count).Bytes;
        }

        private const int _firstLeafIndexAsNodeIndex = int.MaxValue / 2;
        
        internal int GetLeafIndex(int nodeIndex)
        {
            if (nodeIndex == int.MaxValue ||
                nodeIndex < _firstLeafIndexAsNodeIndex)
            {
                throw new IndexOutOfRangeException($"Leaf indices start at {_firstLeafIndexAsNodeIndex}");
            }
            
            return nodeIndex - _firstLeafIndexAsNodeIndex;
        }
        
        [MethodImpl(MethodImplOptions.Synchronized)]
        public void Insert(Bytes32 leaf)
        {
            _keyValueStore[Rlp.Encode(Count).Bytes] = leaf.AsSpan().ToArray();
            Count++;
            StoreCountInTheDb();
        }
        
        public MerkleTreeNode[] GetProof(int leafIndex)
        {
            throw new NotImplementedException();
        }

        // public static IList<IList<Bytes32>> CalculateMerkleTreeFromLeaves(IEnumerable<Bytes32> values,
        //     int layerCount = 32)
        // {
        //     List<Bytes32> workingValues = new List<Bytes32>(values);
        //     List<IList<Bytes32>> tree = new List<IList<Bytes32>>(new[] {workingValues.ToArray()});
        //     for (int height = 0; height < layerCount; height++)
        //     {
        //         if (workingValues.Count % 2 == 1)
        //         {
        //             workingValues.Add(new Bytes32(s_zeroHashes[height]));
        //         }
        //
        //         List<Bytes32> hashes = new List<Bytes32>();
        //         for (int index = 0; index < workingValues.Count; index += 2)
        //         {
        //             byte[] hash = Hash(workingValues[index].AsSpan(), workingValues[index + 1].AsSpan());
        //             hashes.Add(new Bytes32(hash));
        //         }
        //
        //         tree.Add(hashes.ToArray());
        //         workingValues = hashes;
        //     }
        //
        //     return tree;
        // }
        //
        // private static MerkleTreeNode[] GetMerkleProof(IList<IList<Bytes32>> tree, int itemIndex, int? treeLength = null)
        // {
        //     List<Bytes32> proof = new List<Bytes32>();
        //     for (int height = 0; height < (treeLength ?? tree.Count); height++)
        //     {
        //         int subindex = (itemIndex / (1 << height)) ^ 1;
        //         Bytes32 value = subindex < tree[height].Count
        //             ? tree[height][subindex]
        //             : new Bytes32(s_zeroHashes[height]);
        //         proof.Add(value);
        //     }
        //
        //     return proof;
        // }

        private static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Span<byte> combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return s_hashAlgorithm.ComputeHash(combined.ToArray());
        }
    }
}