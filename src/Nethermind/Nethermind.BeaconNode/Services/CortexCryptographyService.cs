﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Cortex.Containers;
using Cortex.Cryptography;

namespace Cortex.BeaconNode.Services
{
    /// <summary>
    /// Implementation of ICryptographyService that uses the Cortex BLS nuget package
    /// </summary>
    public class CortexCryptographyService : ICryptographyService
    {
        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        public Func<BLSParameters, BLS> SignatureAlgorithmFactory { get; set; } = blsParameters => BLS.Create(blsParameters);

        public BlsPublicKey BlsAggregatePublicKeys(IEnumerable<BlsPublicKey> publicKeys)
        {
            var publicKeysSpan = new Span<byte>(new byte[publicKeys.Count() * BlsPublicKey.Length]);
            var publicKeysSpanIndex = 0;
            foreach (var publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += BlsPublicKey.Length;
            }
            using var signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            var aggregatePublicKey = new Span<byte>(new byte[BlsPublicKey.Length]);
            var success = signatureAlgorithm.TryAggregatePublicKeys(publicKeysSpan, aggregatePublicKey, out var bytesWritten);
            if (!success || bytesWritten != BlsPublicKey.Length)
            {
                throw new Exception("Error generating aggregate public key.");
            }
            return new BlsPublicKey(aggregatePublicKey);
        }

        public bool BlsVerify(BlsPublicKey publicKey, Hash32 messageHash, BlsSignature signature, Domain domain)
        {
            var blsParameters = new BLSParameters() { PublicKey = publicKey.AsSpan().ToArray() };
            using var signatureAlgorithm = SignatureAlgorithmFactory(blsParameters);
            return signatureAlgorithm.VerifyHash(messageHash.AsSpan(), signature.AsSpan(), domain.AsSpan());
        }

        public bool BlsVerifyMultiple(IEnumerable<BlsPublicKey> publicKeys, IEnumerable<Hash32> messageHashes, BlsSignature signature, Domain domain)
        {
            var count = publicKeys.Count();

            var publicKeysSpan = new Span<byte>(new byte[count * BlsPublicKey.Length]);
            var publicKeysSpanIndex = 0;
            foreach (var publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += BlsPublicKey.Length;
            }

            var messageHashesSpan = new Span<byte>(new byte[count * Hash32.Length]);
            var messageHashesSpanIndex = 0;
            foreach (var messageHash in messageHashes)
            {
                messageHash.AsSpan().CopyTo(messageHashesSpan.Slice(messageHashesSpanIndex));
                messageHashesSpanIndex += Hash32.Length;
            }

            using var signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            return signatureAlgorithm.VerifyAggregate(publicKeysSpan, messageHashesSpan, signature.AsSpan(), domain.AsSpan());
        }

        public Hash32 Hash(Hash32 a, Hash32 b)
        {
            var input = new Span<byte>(new byte[Hash32.Length * 2]);
            a.AsSpan().CopyTo(input);
            b.AsSpan().CopyTo(input.Slice(Hash32.Length));
            return Hash(input);
        }

        public Hash32 Hash(ReadOnlySpan<byte> bytes)
        {
            var result = new Span<byte>(new byte[Hash32.Length]);
            var success = s_hashAlgorithm.TryComputeHash(bytes, result, out var bytesWritten);
            if (!success || bytesWritten != Hash32.Length)
            {
                throw new Exception("Error generating hash value.");
            }
            return new Hash32(result);
        }
    }
}
