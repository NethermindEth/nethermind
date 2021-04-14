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

using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.DataMarketplace.Infrastructure.Rlp;
using Nethermind.DataMarketplace.Providers.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Providers.Infrastructure.Rlp
{
    internal class PaymentClaimDecoder : IRlpNdmDecoder<PaymentClaim>
    {
        public static void Init()
        {
            // here to register with RLP in static constructor
        }
        
        static PaymentClaimDecoder()
        {
            Nethermind.Serialization.Rlp.Rlp.Decoders[typeof(PaymentClaim)] = new PaymentClaimDecoder();
        }

        public PaymentClaim Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            _ = rlpStream.ReadSequenceLength();
            var id = rlpStream.DecodeKeccak();
            var depositId = rlpStream.DecodeKeccak();
            var assetId = rlpStream.DecodeKeccak();
            var assetName = rlpStream.DecodeString();
            var units = rlpStream.DecodeUInt();
            var claimedUnits = rlpStream.DecodeUInt();
            var unitsRange = Nethermind.Serialization.Rlp.Rlp.Decode<UnitsRange>(rlpStream);
            var value = rlpStream.DecodeUInt256();
            var claimedValue = rlpStream.DecodeUInt256();
            var expiryTime = rlpStream.DecodeUInt();
            var pepper = rlpStream.DecodeByteArray();
            var provider = rlpStream.DecodeAddress();
            var consumer = rlpStream.DecodeAddress();
            var transactions = Nethermind.Serialization.Rlp.Rlp.DecodeArray<TransactionInfo>(rlpStream);
            var transactionCost = rlpStream.DecodeUInt256();
            var timestamp = rlpStream.DecodeUlong();
            var status = (PaymentClaimStatus) rlpStream.DecodeInt();
            var signature = SignatureDecoder.DecodeSignature(rlpStream);
            var paymentClaim = new PaymentClaim(id, depositId, assetId, assetName, units, claimedUnits, unitsRange,
                value, claimedValue, expiryTime, pepper, provider, consumer, signature, timestamp, transactions,
                status);

            if (status == PaymentClaimStatus.Claimed)
            {
                paymentClaim.SetTransactionCost(transactionCost);
            }

            return paymentClaim;
        }

        public Nethermind.Serialization.Rlp.Rlp Encode(PaymentClaim? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Nethermind.Serialization.Rlp.Rlp.OfEmptySequence;
            }

            return Nethermind.Serialization.Rlp.Rlp.Encode(
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Id),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.DepositId),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.AssetId),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.AssetName),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Units),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ClaimedUnits),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.UnitsRange),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Value),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ClaimedValue),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.ExpiryTime),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Pepper),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Provider),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Consumer),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Transactions.ToArray()),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.TransactionCost),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Timestamp),
                Nethermind.Serialization.Rlp.Rlp.Encode((int) item.Status),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Signature.V),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Nethermind.Serialization.Rlp.Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }
        
        public int GetLength(PaymentClaim item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }

        public void Encode(RlpStream stream, PaymentClaim item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }
    }
}