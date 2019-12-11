using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Cortex.Cryptography;
using Nethermind.BeaconNode.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Hash32 = Nethermind.Core2.Types.Hash32;

namespace Nethermind.BeaconNode.Tests
{
    public static class TestSecurity
    {
        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        private static readonly byte[][] s_zeroHashes = new byte[32][];

        static TestSecurity()
        {
            s_zeroHashes[0] = new byte[32];
            for (var index = 1; index < 32; index++)
            {
                s_zeroHashes[index] = Hash(s_zeroHashes[index - 1], s_zeroHashes[index - 1]);
            }
        }

        public static Func<BLSParameters, BLS> SignatureAlgorithmFactory { get; set; } = blsParameters => BLS.Create(blsParameters);

        public static BlsSignature BlsAggregateSignatures(IList<BlsSignature> signatures)
        {
            var signaturesSpan = new Span<byte>(new byte[signatures.Count * 96]);
            for (var index = 0; index < signatures.Count; index++)
            {
                signatures[index].AsSpan().CopyTo(signaturesSpan.Slice(index * 96));
            }
            var aggregateSignatureSpan = new byte[96];
            using var signingAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            var success = signingAlgorithm.TryAggregateSignatures(signaturesSpan, aggregateSignatureSpan, out var bytesWritten);
            var aggregateSignature = new BlsSignature(aggregateSignatureSpan);
            return aggregateSignature;
        }

        public static BlsSignature BlsSign(Hash32 messageHash, byte[] privateKey, Domain domain)
        {
            var parameters = new BLSParameters() { PrivateKey = privateKey };
            using var signingAlgorithm = SignatureAlgorithmFactory(parameters);
            var destination = new byte[96];
            var success = signingAlgorithm.TrySignHash(messageHash.AsSpan(), destination, out var bytesWritten, domain.AsSpan().ToArray());
            return new BlsSignature(destination);
        }

        public static IList<IList<Hash32>> CalculateMerkleTreeFromLeaves(IEnumerable<Hash32> values, int layerCount = 32)
        {
            var workingValues = new List<Hash32>(values);
            var tree = new List<IList<Hash32>>(new[] { workingValues.ToArray() });
            for (var height = 0; height < layerCount; height++)
            {
                if (workingValues.Count % 2 == 1)
                {
                    workingValues.Add(new Hash32(s_zeroHashes[height]));
                }
                var hashes = new List<Hash32>();
                for (var index = 0; index < workingValues.Count; index += 2)
                {
                    var hash = Hash(workingValues[index].AsSpan(), workingValues[index + 1].AsSpan());
                    hashes.Add(new Hash32(hash));
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
                    : new Hash32(s_zeroHashes[height]);
                proof.Add(value);
            }
            return proof;
        }

        public static byte[] Hash(ReadOnlySpan<byte> bytes)
        {
            return s_hashAlgorithm.ComputeHash(bytes.ToArray());
        }

        public static byte[] Hash(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
        {
            var combined = new Span<byte>(new byte[64]);
            a.CopyTo(combined);
            b.CopyTo(combined.Slice(32));
            return s_hashAlgorithm.ComputeHash(combined.ToArray());
        }
    }
}
