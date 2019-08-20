/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using Nethermind.Core.Encoding;
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
            Nethermind.Core.Encoding.Rlp.Decoders[typeof(DepositDetails)] = new DepositDetailsDecoder();
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

                var deposit = Nethermind.Core.Encoding.Rlp.Decode<Deposit>(rlpStream);
                var dataAsset = Nethermind.Core.Encoding.Rlp.Decode<DataAsset>(rlpStream);
                var consumer = rlpStream.DecodeAddress();
                var pepper = rlpStream.DecodeByteArray();
                var timestamp = rlpStream.DecodeUInt();
                var transactionHash = rlpStream.DecodeKeccak();
                var confirmationTimestamp = rlpStream.DecodeUInt();
                var rejected = rlpStream.DecodeBool();
                var earlyRefundTicket = Nethermind.Core.Encoding.Rlp.Decode<EarlyRefundTicket>(rlpStream);
                var claimedRefundTransactionHash = rlpStream.DecodeKeccak();
                var refundClaimed = rlpStream.DecodeBool();
                var kyc = rlpStream.DecodeString();
                var confirmations = rlpStream.DecodeUInt();
                var requiredConfirmations = rlpStream.DecodeUInt();

                return new DepositDetails(deposit, dataAsset, consumer, pepper, timestamp, transactionHash,
                    confirmationTimestamp, rejected, earlyRefundTicket, claimedRefundTransactionHash, refundClaimed,
                    kyc, confirmations, requiredConfirmations);
            }
            catch (Exception)
            {
                rlpStream.Position = 0;
                var sequenceLength = rlpStream.ReadSequenceLength();
                if (sequenceLength == 0)
                {
                    return null;
                }

                var deposit = Nethermind.Core.Encoding.Rlp.Decode<Deposit>(rlpStream);
                var dataAsset = Nethermind.Core.Encoding.Rlp.Decode<DataAsset>(rlpStream);
                var consumer = rlpStream.DecodeAddress();
                var pepper = rlpStream.DecodeByteArray();
                var transactionHash = rlpStream.DecodeKeccak();
                var confirmationTimestamp = rlpStream.DecodeUInt();
                var rejected = rlpStream.DecodeBool();
                var earlyRefundTicket = Nethermind.Core.Encoding.Rlp.Decode<EarlyRefundTicket>(rlpStream);
                var claimedRefundTransactionHash = rlpStream.DecodeKeccak();
                var refundClaimed = rlpStream.DecodeBool();
                var kyc = rlpStream.DecodeString();
                var confirmations = rlpStream.DecodeUInt();
                var requiredConfirmations = rlpStream.DecodeUInt();
                uint timestamp = 0;
                if (rlpStream.Position != rlpStream.Data.Length)
                {
                    timestamp = rlpStream.DecodeUInt();
                }

                return new DepositDetails(deposit, dataAsset, consumer, pepper, timestamp, transactionHash,
                    confirmationTimestamp, rejected, earlyRefundTicket, claimedRefundTransactionHash, refundClaimed,
                    kyc, confirmations, requiredConfirmations);
            }
        }

        public Nethermind.Core.Encoding.Rlp Encode(DepositDetails item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Core.Encoding.Rlp.OfEmptySequence;
            }

            return Nethermind.Core.Encoding.Rlp.Encode(
                Nethermind.Core.Encoding.Rlp.Encode(item.Deposit),
                Nethermind.Core.Encoding.Rlp.Encode(item.DataAsset),
                Nethermind.Core.Encoding.Rlp.Encode(item.Consumer),
                Nethermind.Core.Encoding.Rlp.Encode(item.Pepper),
                Nethermind.Core.Encoding.Rlp.Encode(item.Timestamp),
                Nethermind.Core.Encoding.Rlp.Encode(item.TransactionHash),
                Nethermind.Core.Encoding.Rlp.Encode(item.ConfirmationTimestamp),
                Nethermind.Core.Encoding.Rlp.Encode(item.Rejected),
                Nethermind.Core.Encoding.Rlp.Encode(item.EarlyRefundTicket),
                Nethermind.Core.Encoding.Rlp.Encode(item.ClaimedRefundTransactionHash),
                Nethermind.Core.Encoding.Rlp.Encode(item.RefundClaimed),
                Nethermind.Core.Encoding.Rlp.Encode(item.Kyc),
                Nethermind.Core.Encoding.Rlp.Encode(item.Confirmations),
                Nethermind.Core.Encoding.Rlp.Encode(item.RequiredConfirmations));
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