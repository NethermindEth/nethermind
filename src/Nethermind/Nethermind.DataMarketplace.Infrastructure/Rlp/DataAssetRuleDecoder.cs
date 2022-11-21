// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetRuleDecoder : IRlpNdmDecoder<DataAssetRule?>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataAssetRuleDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataAssetRule)] = new DataAssetRuleDecoder();
        }

        public DataAssetRule? Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            int sequenceLength = rlpStream.ReadSequenceLength();
            if (sequenceLength == 0)
            {
                return null;
            }

            UInt256 value = rlpStream.DecodeUInt256();
            return new DataAssetRule(value);
        }

        public void Encode(RlpStream stream, DataAssetRule? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataAssetRule? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Value));
        }

        public int GetLength(DataAssetRule? item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
