// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp
{
    public class DepositDetailsDecoder : IRlpNdmDecoder<DepositDetails>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DepositDetailsDecoder()
        {
            Serialization.Rlp.Rlp.Decoders[typeof(DepositDetails)] = new DepositDetailsDecoder();
        }

        public DepositDetails Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.ReadSequenceLength();
            Deposit deposit = Serialization.Rlp.Rlp.Decode<Deposit>(rlpStream);
            DataAsset dataAsset = Serialization.Rlp.Rlp.Decode<DataAsset>(rlpStream);
            Address consumer = rlpStream.DecodeAddress();
            var pepper = rlpStream.DecodeByteArray();
            uint timestamp = rlpStream.DecodeUInt();
            var transactions = Serialization.Rlp.Rlp.DecodeArray<TransactionInfo>(rlpStream);
            uint confirmationTimestamp = rlpStream.DecodeUInt();
            bool rejected = rlpStream.DecodeBool();
            bool cancelled = rlpStream.DecodeBool();
            EarlyRefundTicket earlyRefundTicket = Serialization.Rlp.Rlp.Decode<EarlyRefundTicket>(rlpStream);
            var claimedRefundTransactions = Serialization.Rlp.Rlp.DecodeArray<TransactionInfo>(rlpStream);
            bool refundClaimed = rlpStream.DecodeBool();
            bool refundCancelled = rlpStream.DecodeBool();
            string kyc = rlpStream.DecodeString();
            uint confirmations = rlpStream.DecodeUInt();
            uint requiredConfirmations = rlpStream.DecodeUInt();

            return new DepositDetails(deposit, dataAsset, consumer, pepper, timestamp, transactions,
                confirmationTimestamp, rejected, cancelled, earlyRefundTicket, claimedRefundTransactions,
                refundClaimed, refundCancelled, kyc, confirmations, requiredConfirmations);
        }

        public void Encode(RlpStream stream, DepositDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public Serialization.Rlp.Rlp Encode(DepositDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            return Serialization.Rlp.Rlp.Encode(
                Serialization.Rlp.Rlp.Encode(item.Deposit),
                Serialization.Rlp.Rlp.Encode(item.DataAsset),
                Serialization.Rlp.Rlp.Encode(item.Consumer),
                Serialization.Rlp.Rlp.Encode(item.Pepper),
                Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Serialization.Rlp.Rlp.Encode(item.Transactions.ToArray()),
                Serialization.Rlp.Rlp.Encode(item.ConfirmationTimestamp),
                Serialization.Rlp.Rlp.Encode(item.Rejected),
                Serialization.Rlp.Rlp.Encode(item.Cancelled),
                Serialization.Rlp.Rlp.Encode(item.EarlyRefundTicket),
                Serialization.Rlp.Rlp.Encode(item.ClaimedRefundTransactions.ToArray()),
                Serialization.Rlp.Rlp.Encode(item.RefundClaimed),
                Serialization.Rlp.Rlp.Encode(item.RefundCancelled),
                Serialization.Rlp.Rlp.Encode(item.Kyc),
                Serialization.Rlp.Rlp.Encode(item.Confirmations),
                Serialization.Rlp.Rlp.Encode(item.RequiredConfirmations));
        }

        public int GetLength(DepositDetails item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
