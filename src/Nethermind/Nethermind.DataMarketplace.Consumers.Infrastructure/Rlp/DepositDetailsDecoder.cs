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
using System.IO;
using System.Linq;
using Nethermind.Core.Serialization;
using Nethermind.DataMarketplace.Consumers.Deposits.Domain;
using Nethermind.DataMarketplace.Core.Domain;

namespace Nethermind.DataMarketplace.Consumers.Infrastructure.Rlp
{
    public class DepositDetailsDecoder : IRlpDecoder<DepositDetails>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }

        static DepositDetailsDecoder()
        {
            Nethermind.Core.Serialization.Rlp.Decoders[typeof(DepositDetails)] = new DepositDetailsDecoder();
        }

        public DepositDetails Decode(RlpStream rlpStream,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            try
            {
                var sequenceLength = rlpStream.ReadSequenceLength();
                if (sequenceLength == 0)
                {
                    return null;
                }

                var deposit = Nethermind.Core.Serialization.Rlp.Decode<Deposit>(rlpStream);
                var dataAsset = Nethermind.Core.Serialization.Rlp.Decode<DataAsset>(rlpStream);
                var consumer = rlpStream.DecodeAddress();
                var pepper = rlpStream.DecodeByteArray();
                var timestamp = rlpStream.DecodeUInt();
                var transactions = Nethermind.Core.Serialization.Rlp.DecodeArray<TransactionInfo>(rlpStream);
                var confirmationTimestamp = rlpStream.DecodeUInt();
                var rejected = rlpStream.DecodeBool();
                var cancelled = rlpStream.DecodeBool();
                var earlyRefundTicket = Nethermind.Core.Serialization.Rlp.Decode<EarlyRefundTicket>(rlpStream);
                var claimedRefundTransactions = Nethermind.Core.Serialization.Rlp.DecodeArray<TransactionInfo>(rlpStream);
                var refundClaimed = rlpStream.DecodeBool();
                var refundCancelled = rlpStream.DecodeBool();
                var kyc = rlpStream.DecodeString();
                var confirmations = rlpStream.DecodeUInt();
                var requiredConfirmations = rlpStream.DecodeUInt();

                return new DepositDetails(deposit, dataAsset, consumer, pepper, timestamp, transactions,
                    confirmationTimestamp, rejected, cancelled, earlyRefundTicket, claimedRefundTransactions,
                    refundClaimed, refundCancelled, kyc, confirmations, requiredConfirmations);
            }
            catch (Exception)
            {
                rlpStream.Position = 0;
                var sequenceLength = rlpStream.ReadSequenceLength();
                if (sequenceLength == 0)
                {
                    return null;
                }

                var deposit = Nethermind.Core.Serialization.Rlp.Decode<Deposit>(rlpStream);
                var dataAsset = Nethermind.Core.Serialization.Rlp.Decode<DataAsset>(rlpStream);
                var consumer = rlpStream.DecodeAddress();
                var pepper = rlpStream.DecodeByteArray();
                var transactions = Nethermind.Core.Serialization.Rlp.DecodeArray<TransactionInfo>(rlpStream);
                var confirmationTimestamp = rlpStream.DecodeUInt();
                var rejected = rlpStream.DecodeBool();
                var cancelled = rlpStream.DecodeBool();
                var earlyRefundTicket = Nethermind.Core.Serialization.Rlp.Decode<EarlyRefundTicket>(rlpStream);
                var claimedRefundTransactions = Nethermind.Core.Serialization.Rlp.DecodeArray<TransactionInfo>(rlpStream);
                var refundClaimed = rlpStream.DecodeBool();
                var refundCancelled = rlpStream.DecodeBool();
                var kyc = rlpStream.DecodeString();
                var confirmations = rlpStream.DecodeUInt();
                var requiredConfirmations = rlpStream.DecodeUInt();
                uint timestamp = 0;
                if (rlpStream.Position != rlpStream.Data.Length)
                {
                    timestamp = rlpStream.DecodeUInt();
                }

                return new DepositDetails(deposit, dataAsset, consumer, pepper, timestamp, transactions,
                    confirmationTimestamp, rejected, cancelled, earlyRefundTicket, claimedRefundTransactions,
                    refundClaimed, refundCancelled, kyc, confirmations, requiredConfirmations);
            }
        }

        public Nethermind.Core.Serialization.Rlp Encode(DepositDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Serialization.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Serialization.Rlp.Encode(
                Nethermind.Core.Serialization.Rlp.Encode(item.Deposit),
                Nethermind.Core.Serialization.Rlp.Encode(item.DataAsset),
                Nethermind.Core.Serialization.Rlp.Encode(item.Consumer),
                Nethermind.Core.Serialization.Rlp.Encode(item.Pepper),
                Nethermind.Core.Serialization.Rlp.Encode(item.Timestamp),
                Nethermind.Core.Serialization.Rlp.Encode(item.Transactions.ToArray()),
                Nethermind.Core.Serialization.Rlp.Encode(item.ConfirmationTimestamp),
                Nethermind.Core.Serialization.Rlp.Encode(item.Rejected),
                Nethermind.Core.Serialization.Rlp.Encode(item.Cancelled),
                Nethermind.Core.Serialization.Rlp.Encode(item.EarlyRefundTicket),
                Nethermind.Core.Serialization.Rlp.Encode(item.ClaimedRefundTransactions.ToArray()),
                Nethermind.Core.Serialization.Rlp.Encode(item.RefundClaimed),
                Nethermind.Core.Serialization.Rlp.Encode(item.RefundCancelled),
                Nethermind.Core.Serialization.Rlp.Encode(item.Kyc),
                Nethermind.Core.Serialization.Rlp.Encode(item.Confirmations),
                Nethermind.Core.Serialization.Rlp.Encode(item.RequiredConfirmations));
        }

        public void Encode(MemoryStream stream, DepositDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public int GetLength(DepositDetails item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}