//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Mev.Source
{
    public class CarriedTxExtractor
    {
        public CarriedTxExtractor(PrivateKey ourValidatorPrivateKey, ILogger? logger)
        {
            _ourValidatorPrivateKey = ourValidatorPrivateKey;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _ourValidatorPublicKey = _ourValidatorPrivateKey.PublicKey;
        }

        private readonly PublicKey _ourValidatorPublicKey;

        private readonly byte[] _mevPrefix = new byte[0];

        private readonly BundleDecoder _bundleDecoder = new();
        
        private readonly EciesCipher _cipher = new(new CryptoRandom());
        private readonly PrivateKey _ourValidatorPrivateKey;
        private readonly ILogger _logger;

        public MevBundle? ExtractBundleFromCarrier(Transaction carrierTx)
        {
            MevBundle? result = null;
            // constants somewhere
            int mevPrefixLength = _mevPrefix.Length;
            int metadataLength = _mevPrefix.Length + PublicKey.LengthInBytes;
            
            byte[]? calldata = carrierTx.Data;
            if (calldata is null)
            {
                // do nothing
            }
            else if (calldata.Length < metadataLength)
            {
                // do nothing
            }
            else
            {
                Span<byte> prefix = calldata.AsSpan(mevPrefixLength);
                bool hasMevPrefix = prefix.SequenceEqual(_mevPrefix);
                if (hasMevPrefix)
                {
                    Span<byte> validatorKey = calldata.AsSpan(mevPrefixLength, PublicKey.LengthInBytes);
                    bool isAddressedToOurValidator = validatorKey.SequenceEqual(_ourValidatorPublicKey.Bytes);
                    if (isAddressedToOurValidator)
                    {
                        // here was a question about the latency of decrypting
                        // I believe it is negligible (taking into account that transaction is paid for)
                        byte[] bundleRlpCiphertext = calldata.Slice(mevPrefixLength);
                        (bool success, byte[] bundleRlpBytes) =
                            _cipher.Decrypt(_ourValidatorPrivateKey, bundleRlpCiphertext);
                        if (success)
                        {
                            // this would actually use a bundle decoder that is missing for now
                            result = _bundleDecoder.Decode(new RlpStream(bundleRlpBytes));
                        }
                        else
                        {
                            if (_logger.IsDebug)
                                _logger.Debug(
                                    "Failed to decrypt payload of a transaction looking like a carrier transaction");
                        }
                    }
                }
            }

            return result;
        }
        
        private class BundleDecoder : IRlpStreamDecoder<MevBundle>
        {
            public int GetLength(MevBundle item, RlpBehaviors rlpBehaviors)
            {
                throw new NotImplementedException();
            }

            public MevBundle Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                throw new NotImplementedException();
            }

            public void Encode(RlpStream stream, MevBundle item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
            {
                throw new NotImplementedException();
            }
        }
    }
}
