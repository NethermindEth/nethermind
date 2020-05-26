using System.Collections.Generic;
using System.Security.Cryptography;
using System;

namespace Nethermind.Baseline
{
    public class MerkleTree
    {

        private static readonly byte[][] s_zeroHashes = new byte[32][];
        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        static MerkleTree() {

            s_zeroHashes[0] = new byte[32];
            for (int index = 1; index < 32; index++)
            {
                s_zeroHashes[index] = Hash(s_zeroHashes[index - 1], s_zeroHashes[index - 1]);
            }
        }
        
        public static IList<IList<Bytes32>> CalculateMerkleTreeFromLeaves(IEnumerable<Bytes32> values,
            int layerCount = 32)
        {
            List<Bytes32> workingValues = new List<Bytes32>(values);
            List<IList<Bytes32>> tree = new List<IList<Bytes32>>(new[] {workingValues.ToArray()});
            for (int height = 0; height < layerCount; height++)
            {
                if (workingValues.Count % 2 == 1)
                {
                    workingValues.Add(new Bytes32(s_zeroHashes[height]));
                }

                List<Bytes32> hashes = new List<Bytes32>();
                for (int index = 0; index < workingValues.Count; index += 2)
                {
                    byte[] hash = Hash(workingValues[index].AsSpan(), workingValues[index + 1].AsSpan());
                    hashes.Add(new Bytes32(hash));
                }

                tree.Add(hashes.ToArray());
                workingValues = hashes;
            }

            return tree;
        }

        private static IList<Bytes32> GetMerkleProof(IList<IList<Bytes32>> tree, int itemIndex, int? treeLength = null)
        {
            List<Bytes32> proof = new List<Bytes32>();
            for (int height = 0; height < (treeLength ?? tree.Count); height++)
            {
                int subindex = (itemIndex / (1 << height)) ^ 1;
                Bytes32 value = subindex < tree[height].Count
                    ? tree[height][subindex]
                    : new Bytes32(s_zeroHashes[height]);
                proof.Add(value);
            }

            return proof;
        }

        private static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            Span<byte> combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return s_hashAlgorithm.ComputeHash(combined.ToArray());
        }
    }
}