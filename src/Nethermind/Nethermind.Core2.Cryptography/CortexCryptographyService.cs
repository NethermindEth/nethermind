//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Cortex.Cryptography;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography
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
            // TKS: added an extension here as an example to discuss - I have been avoiding passing IEnumerable
            // for the performance reasons - to avoid multiple runs
            // and opted for passing either arrays or lists and keep it consistent
            // it sometimes / very rarely has an issue of having to cast list to an array
            // but usually we have a full control over the flow so it ends up being much better
            // what do you think?
            var publicKeysSpan = new Span<byte>(new byte[publicKeys.Count() * BlsPublicKey.Length]);
            var publicKeysSpanIndex = 0;
            foreach (var publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += BlsPublicKey.Length;
            }
            using var signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            var aggregatePublicKey = new byte[BlsPublicKey.Length];
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
