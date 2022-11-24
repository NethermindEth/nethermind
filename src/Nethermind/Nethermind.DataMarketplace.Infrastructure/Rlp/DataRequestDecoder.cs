// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataRequestDecoder : IRlpNdmDecoder<DataRequest>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataRequestDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataRequest)] = new DataRequestDecoder();
        }

        public DataRequest Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak assetId = rlpStream.DecodeKeccak();
            uint units = rlpStream.DecodeUInt();
            UInt256 value = rlpStream.DecodeUInt256();
            uint expiryTime = rlpStream.DecodeUInt();
            var salt = rlpStream.DecodeByteArray();
            Address provider = rlpStream.DecodeAddress();
            Address consumer = rlpStream.DecodeAddress();
            Signature signature = SignatureDecoder.DecodeSignature(rlpStream);

            return new DataRequest(assetId, units, value, expiryTime, salt, provider, consumer, signature);
        }

        public void Encode(RlpStream stream, DataRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataRequest item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.DataAssetId),
                Serialization.Rlp.Rlp.Encode(item.Units),
                Serialization.Rlp.Rlp.Encode(item.Value),
                Serialization.Rlp.Rlp.Encode(item.ExpiryTime),
                Serialization.Rlp.Rlp.Encode(item.Pepper),
                Serialization.Rlp.Rlp.Encode(item.Provider),
                Serialization.Rlp.Rlp.Encode(item.Consumer),
                Serialization.Rlp.Rlp.Encode(item.Signature.V),
                Serialization.Rlp.Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Serialization.Rlp.Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }

        public int GetLength(DataRequest item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
