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
using System.Numerics;
using System.Threading.Tasks;
using Nethermind.Cryptography;
using Microsoft.Extensions.Options;
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Nethermind.HonestValidator.Services;

namespace Nethermind.HonestValidator.MockedStart
{
    public class QuickStartKeyProvider : IValidatorKeyProvider, IDisposable
    {
        private static readonly BigInteger s_curveOrder = BigInteger.Parse("52435875175126190479447740508185965837690552500527637822603658699938581184513");
        
        private readonly IOptionsMonitor<QuickStartParameters> _quickStartParameterOptions;
        
        private readonly IDictionary<BlsPublicKey, BLS> _publicKeyToBls = new Dictionary<BlsPublicKey, BLS>();

        public QuickStartKeyProvider(
            IOptionsMonitor<QuickStartParameters> quickStartParameterOptions)
        {
            _quickStartParameterOptions = quickStartParameterOptions;
        }

        public IList<BlsPublicKey> GetPublicKeys()
        {
            if (_publicKeyToBls.Count == 0)
            {
                QuickStartParameters _quickStartParameters = _quickStartParameterOptions.CurrentValue;

                ulong endIndex = _quickStartParameters.ValidatorStartIndex + _quickStartParameters.NumberOfValidators;
                for (ulong validatorIndex = _quickStartParameters.ValidatorStartIndex;
                    validatorIndex < endIndex;
                    validatorIndex++)
                {
                    byte[] privateKey = GeneratePrivateKey(validatorIndex);

                    BLSParameters blsParameters = new BLSParameters()
                    {
                        PrivateKey = privateKey
                    };
                    BLS bls = BLS.Create(blsParameters);
                    byte[] publicKeyBytes = new byte[BlsPublicKey.Length];
                    bls.TryExportBlsPublicKey(publicKeyBytes, out int publicKeyBytesWritten);
                    BlsPublicKey publicKey = new BlsPublicKey(publicKeyBytes);

                    // Cache the BLS class, for easy signing
                    _publicKeyToBls[publicKey] = bls;
                }
            }

            return _publicKeyToBls.Keys.ToList();
        }

        public BlsSignature SignRoot(BlsPublicKey blsPublicKey, Root root)
        {
            if (_publicKeyToBls.Count == 0)
            {
                var keyCount = GetPublicKeys().Count();
            }
            
            BLS bls = _publicKeyToBls[blsPublicKey];
            
            byte[] destination = new byte[BlsSignature.Length];
            bool success = bls.TrySignData(root.AsSpan(), destination, out int bytesWritten);
            if (!success || bytesWritten != BlsSignature.Length)
            {
                throw new Exception($"Failure signing hash {root} for public key {blsPublicKey}.");
            }
            BlsSignature blsSignature = new BlsSignature(destination);
            return blsSignature;
        }
        
        // FIXME: This is duplicate of beacon node, need to clean up
        public byte[] GeneratePrivateKey(ulong index)
        {
            Span<byte> input = new Span<byte>(new byte[32]);
            BigInteger bigIndex = new BigInteger(index);
            bool indexWriteSuccess = bigIndex.TryWriteBytes(input, out int indexBytesWritten, isUnsigned: true, isBigEndian: false);
            if (!indexWriteSuccess || indexBytesWritten == 0)
            {
                throw new Exception("Error getting input for quick start private key generation.");
            }

            Bytes32 bytes32 = Sha256.Compute(input);
            ReadOnlySpan<byte> hash = bytes32.AsSpan();
            // Mocked start interop specifies to convert the hash as little endian (which is the default for BigInteger)
            BigInteger value = new BigInteger(hash.ToArray(), isUnsigned: true);
            BigInteger privateKey = value % s_curveOrder;

            // Note that the private key is an *unsigned*, *big endian* number
            // However, we want to pad the big endian on the left to get 32 bytes.
            // So, write as little endian (will pad to right), then reverse.
            // NOTE: Alternative, write to Span 64, and then slice based on bytesWritten to get the padding.
            Span<byte> privateKeySpan = new Span<byte>(new byte[32]);
            bool keyWriteSuccess = privateKey.TryWriteBytes(privateKeySpan, out int keyBytesWritten, isUnsigned: true, isBigEndian: false);
            if (!keyWriteSuccess)
            {
                throw new Exception("Error generating quick start private key.");
            }
            privateKeySpan.Reverse();
            
            return privateKeySpan.ToArray();
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                var blsToDispose = _publicKeyToBls.Values;
                _publicKeyToBls.Clear();
                foreach (var bls in blsToDispose)
                {
                    bls.Dispose();
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            //GC.SuppressFinalize(this);
        }
    }
}