using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Cortex.Containers;

namespace Cortex.BeaconNode.Tests
{
    public static class TestUtility
    {
        private static readonly HashAlgorithm _hashAlgorithm = SHA256.Create();

        private static readonly byte[][] _zeroHashes;

        static TestUtility()
        {
            _zeroHashes = new byte[32][];
            _zeroHashes[0] = new byte[32];
            for (var index = 1; index < 32; index++)
            {
                _zeroHashes[index] = Hash(_zeroHashes[index - 1], _zeroHashes[index - 1]);
            }
        }

        public static byte[] Hash(ReadOnlySpan<byte> bytes)
        {
            return _hashAlgorithm.ComputeHash(bytes.ToArray());
        }

        public static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return _hashAlgorithm.ComputeHash(combined.ToArray());
        }

        public static BlsSignature BlsSign(Hash32 messageHash, byte[] privateKey, Domain domain)
        {
            // TODO: Implement this.
            return new BlsSignature();
        }

        public static IList<IList<Hash32>> CalculateMerkleTreeFromLeaves(IEnumerable<Hash32> values, int layerCount = 32)
        {
            var workingValues = new List<Hash32>(values);
            var tree = new List<IList<Hash32>>(new[] { workingValues.ToArray() });
            for (var height = 0; height < layerCount; height++)
            {
                if (workingValues.Count % 2 == 1)
                {
                    workingValues.Add(_zeroHashes[height]);
                }
                var hashes = new List<Hash32>();
                for (var index = 0; index < workingValues.Count; index += 2)
                {
                    var hash = Hash(workingValues[index], workingValues[index + 1]);
                    hashes.Add(hash);
                }
                tree.Add(hashes.ToArray());
                workingValues = hashes;
            }
            return tree;
        }
        
        public static IList<Hash32> GetMerkleProof(IList<IList<Hash32>> tree, int itemIndex, int? treeLength = null)
        {
            var proof = new List<Hash32>();
            for (var height = 0; height < (treeLength ?? tree.Count); height++)
            {
                var subindex = (itemIndex / (1 << height)) ^ 1;
                var value = subindex < tree[height].Count
                    ? tree[height][subindex]
                    : _zeroHashes[height];
                proof.Add(value);
            }
            return proof;
        }
    }
}
