// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetDecoder : IRlpNdmDecoder<DataAsset>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataAssetDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataAsset)] = new DataAssetDecoder();
        }

        public DataAsset Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                Keccak id = rlpStream.DecodeKeccak();
                string name = rlpStream.DecodeString();
                string description = rlpStream.DecodeString();
                UInt256 unitPrice = rlpStream.DecodeUInt256();
                DataAssetUnitType unitType = (DataAssetUnitType)rlpStream.DecodeInt();
                uint minUnits = rlpStream.DecodeUInt();
                uint maxUnits = rlpStream.DecodeUInt();
                DataAssetRules rules = Serialization.Rlp.Rlp.Decode<DataAssetRules>(rlpStream);
                DataAssetProvider provider = Serialization.Rlp.Rlp.Decode<DataAssetProvider>(rlpStream);
                string file = rlpStream.DecodeString();
                QueryType queryType = (QueryType)rlpStream.DecodeInt();
                DataAssetState state = (DataAssetState)rlpStream.DecodeInt();
                string termsAndConditions = rlpStream.DecodeString();
                bool kycRequired = rlpStream.DecodeBool();
                string plugin = rlpStream.DecodeString();

                return new DataAsset(id, name, description, unitPrice, unitType, minUnits, maxUnits,
                    rules, provider, file, queryType, state, termsAndConditions, kycRequired, plugin);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(DataAsset)} could not be deserialized", e);
            }
        }

        public void Encode(RlpStream stream, DataAsset item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataAsset item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.Name),
                Serialization.Rlp.Rlp.Encode(item.Description),
                Serialization.Rlp.Rlp.Encode(item.UnitPrice),
                Serialization.Rlp.Rlp.Encode((int)item.UnitType),
                Serialization.Rlp.Rlp.Encode(item.MinUnits),
                Serialization.Rlp.Rlp.Encode(item.MaxUnits),
                Serialization.Rlp.Rlp.Encode(item.Rules),
                Serialization.Rlp.Rlp.Encode(item.Provider),
                Serialization.Rlp.Rlp.Encode(item.File),
                Serialization.Rlp.Rlp.Encode((int)item.QueryType),
                Serialization.Rlp.Rlp.Encode((int)item.State),
                Serialization.Rlp.Rlp.Encode(item.TermsAndConditions),
                Serialization.Rlp.Rlp.Encode(item.KycRequired),
                Serialization.Rlp.Rlp.Encode(item.Plugin));
        }

        public int GetLength(DataAsset item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
