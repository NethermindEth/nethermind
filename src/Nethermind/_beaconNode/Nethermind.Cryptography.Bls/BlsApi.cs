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

namespace Nethermind.Cryptography
{
    /// <summary>
    /// BLS signature scheme API as defined in specifications. 
    /// Note that it does not support the additional domain parameter for hash-to-point used in some implementation (e.g. Ethereum 2.0).
    /// </summary>
    public class BlsApi
    {
        private readonly BLS _bls;

        public BlsApi(BLS bls)
        {
            _bls = bls;
        }

        public byte[] Aggregate(IList<byte[]> signatures)
        {
            var signatureLength = signatures.First().Length;
            var signaturesSpan = new Span<byte>(new byte[signatures.Count() * signatureLength]);
            var index = 0;
            foreach (var signature in signatures)
            {
                signature.CopyTo(signaturesSpan.Slice(index * signatureLength));
            }
            var aggregate = new Span<byte>(new byte[signatureLength]);
            var success = _bls.TryAggregateSignatures(signaturesSpan, aggregate, out var bytesWritten);
            return aggregate.ToArray();
        }

        public bool AggregateVerify(IList<Tuple<byte, byte>> publicKeysAndMessages, ReadOnlySpan<byte> signature)
        {
            // Aggregate verify is across multiple public keys...
            // (.NET crypto AsymmetricAlgorithm usually represents one key only)

            throw new NotImplementedException();
        }

        public (byte[] publicKey, byte[] secretKey) KeyGen(ReadOnlySpan<byte> inputKeyMaterial)
        {
            var parameters = new BLSParameters()
            {
                InputKeyMaterial = inputKeyMaterial.ToArray()
            };
            _bls.ImportParameters(parameters);

            throw new NotImplementedException();
            //return (new byte[0], new byte[0]);
        }

        public byte[] Sign(ReadOnlySpan<byte> secretKey, ReadOnlySpan<byte> message)
        {
            var parameters = new BLSParameters()
            {
                PrivateKey = secretKey.ToArray()
            };
            _bls.ImportParameters(parameters);
            var hash = new Span<byte>(new byte[32]);
            var hashSuccess = _bls.HashAlgorithm.TryComputeHash(message, hash, out var hashBytesWritten);
            var signature = new Span<byte>(new byte[96]);
            var signSuccess = _bls.TrySignData(hash, signature, out var signBytesWritten);
            return signature.ToArray();
        }

        public bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
        {
            throw new NotImplementedException();
        }

        // PopProve(SK)
        // PopVerify(PK, proof)
        // FastAggregateVerify(PK1...PKn, message, signature)
    }
}
