// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class DataAssetProviderDecoder : IRlpNdmDecoder<DataAssetProvider>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DataAssetProviderDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DataAssetProvider)] = new DataAssetProviderDecoder();
        }

        public DataAssetProvider Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                rlpStream.ReadSequenceLength();
                Address address = rlpStream.DecodeAddress();
                string name = rlpStream.DecodeString();
                return new DataAssetProvider(address, name);
            }
            catch (Exception e)
            {
                throw new RlpException($"{nameof(DataAssetProvider)} cannot be deserialized from", e);
            }
        }

        public void Encode(RlpStream stream, DataAssetProvider item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DataAssetProvider item,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Address),
                Serialization.Rlp.Rlp.Encode(item.Name));
        }

        public int GetLength(DataAssetProvider item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
