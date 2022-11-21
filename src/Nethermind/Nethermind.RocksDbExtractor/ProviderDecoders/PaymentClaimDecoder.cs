// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.RocksDbExtractor.ProviderDecoders.Domain;
using Nethermind.Serialization.Rlp;

namespace Nethermind.RocksDbExtractor.ProviderDecoders
{
    internal class PaymentClaimDecoder : IRlpDecoder<PaymentClaim>
    {
        static PaymentClaimDecoder()
        {
            Rlp.Decoders[typeof(PaymentClaim)] = new PaymentClaimDecoder();
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
            var transactions = Rlp.DecodeArray<TransactionInfo>(rlpStream);
            var transactionCost = rlpStream.DecodeUInt256();
            var timestamp = rlpStream.DecodeUlong();
            var status = (PaymentClaimStatus)rlpStream.DecodeInt();
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

        public Rlp Encode(PaymentClaim item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            return Rlp.Encode(
                Rlp.Encode(item.Id),
                Rlp.Encode(item.DepositId),
                Rlp.Encode(item.AssetId),
                Rlp.Encode(item.AssetName),
                Rlp.Encode(item.Units),
                Rlp.Encode(item.ClaimedUnits),
                Rlp.Encode(item.UnitsRange),
                Rlp.Encode(item.Value),
                Rlp.Encode(item.ClaimedValue),
                Rlp.Encode(item.ExpiryTime),
                Rlp.Encode(item.Pepper),
                Rlp.Encode(item.Provider),
                Rlp.Encode(item.Consumer),
                Rlp.Encode(item.Transactions.ToArray()),
                Rlp.Encode(item.TransactionCost),
                Rlp.Encode(item.Timestamp),
                Rlp.Encode((int)item.Status),
                Rlp.Encode(item.Signature.V),
                Rlp.Encode(item.Signature.R.WithoutLeadingZeros()),
                Rlp.Encode(item.Signature.S.WithoutLeadingZeros()));
        }

        public int GetLength(PaymentClaim item, RlpBehaviors rlpBehaviors)
        {
            throw new System.NotImplementedException();
        }
    }
}
