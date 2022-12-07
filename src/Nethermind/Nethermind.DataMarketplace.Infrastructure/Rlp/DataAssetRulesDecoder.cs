// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetRulesDecoder : IRlpNdmDecoder<DataAssetRules>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataAssetRulesDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataAssetRules)] = new DataAssetRulesDecoder();
        }

        public DataAssetRules Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                DataAssetRule expiry = Serialization.Rlp.Rlp.Decode<DataAssetRule>(rlpStream);
                DataAssetRule upfrontPayment = Serialization.Rlp.Rlp.Decode<DataAssetRule>(rlpStream);

                return new DataAssetRules(expiry, upfrontPayment);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(DataAssetRules)} could not be deserialized", e);
            }
        }

        public void Encode(RlpStream stream, DataAssetRules item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataAssetRules item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Expiry),
                Serialization.Rlp.Rlp.Encode(item.UpfrontPayment));
        }

        public int GetLength(DataAssetRules item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
