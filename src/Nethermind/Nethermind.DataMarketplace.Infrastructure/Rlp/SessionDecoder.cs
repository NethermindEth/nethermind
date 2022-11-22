// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Infrastructure.Rlp
{
    public class SessionDecoder : IRlpNdmDecoder<Session>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static SessionDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(Session)] = new SessionDecoder();
        }

        public Session Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Keccak id = rlpStream.DecodeKeccak();
            Keccak depositId = rlpStream.DecodeKeccak();
            Keccak dataAssetId = rlpStream.DecodeKeccak();
            Address consumerAddress = rlpStream.DecodeAddress();
            PublicKey consumerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            Address providerAddress = rlpStream.DecodeAddress();
            PublicKey providerNodeId = new PublicKey(rlpStream.DecodeByteArray());
            SessionState state = (SessionState)rlpStream.DecodeInt();
            uint startUnitsFromProvider = rlpStream.DecodeUInt();
            uint startUnitsFromConsumer = rlpStream.DecodeUInt();
            ulong startTimestamp = rlpStream.DecodeUlong();
            ulong finishTimestamp = rlpStream.DecodeUlong();
            uint consumedUnits = rlpStream.DecodeUInt();
            uint unpaidUnits = rlpStream.DecodeUInt();
            uint paidUnits = rlpStream.DecodeUInt();
            uint settledUnits = rlpStream.DecodeUInt();

            return new Session(id, depositId, dataAssetId, consumerAddress, consumerNodeId, providerAddress,
                providerNodeId, state, startUnitsFromConsumer, startUnitsFromProvider, startTimestamp, finishTimestamp,
                consumedUnits, unpaidUnits, paidUnits, settledUnits);
        }

        public void Encode(RlpStream stream, Session item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(Session item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Id),
                Serialization.Rlp.Rlp.Encode(item.DepositId),
                Serialization.Rlp.Rlp.Encode(item.DataAssetId),
                Serialization.Rlp.Rlp.Encode(item.ConsumerAddress),
                Serialization.Rlp.Rlp.Encode(item.ConsumerNodeId.Bytes),
                Serialization.Rlp.Rlp.Encode(item.ProviderAddress),
                Serialization.Rlp.Rlp.Encode(item.ProviderNodeId.Bytes),
                Serialization.Rlp.Rlp.Encode((int)item.State),
                Serialization.Rlp.Rlp.Encode(item.StartUnitsFromProvider),
                Serialization.Rlp.Rlp.Encode(item.StartUnitsFromConsumer),
                Serialization.Rlp.Rlp.Encode(item.StartTimestamp),
                Serialization.Rlp.Rlp.Encode(item.FinishTimestamp),
                Serialization.Rlp.Rlp.Encode(item.ConsumedUnits),
                Serialization.Rlp.Rlp.Encode(item.UnpaidUnits),
                Serialization.Rlp.Rlp.Encode(item.PaidUnits),
                Serialization.Rlp.Rlp.Encode(item.SettledUnits));
        }

        public int GetLength(Session item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
