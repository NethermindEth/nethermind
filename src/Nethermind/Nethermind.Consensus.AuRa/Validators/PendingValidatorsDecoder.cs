// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal sealed class PendingValidatorsDecoder : RlpDecoder<PendingValidators>
    {
        protected override PendingValidators DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int pendingValidatorsCheck = decoderContext.Position + sequenceLength;

            ulong blockNumber = decoderContext.DecodeULong();
            Hash256 blockHash = decoderContext.DecodeKeccak();

            int addressSequenceLength = decoderContext.ReadSequenceLength();
            int addressCheck = decoderContext.Position + addressSequenceLength;
            List<Address> addresses = [];
            while (decoderContext.Position < addressCheck)
            {
                addresses.Add(decoderContext.DecodeAddress());
            }
            decoderContext.Check(addressCheck);

            PendingValidators result = new(blockNumber, blockHash, addresses.ToArray())
            {
                AreFinalized = decoderContext.DecodeBool()
            };

            decoderContext.Check(pendingValidatorsCheck);

            return result;
        }

        public override void Encode<TWriter>(ref TWriter writer, PendingValidators item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            writer.StartSequence(lengths.Read());
            writer.Encode(item.BlockNumber);
            writer.Encode(item.BlockHash);
            writer.StartSequence(lengths.Read());
            for (int i = 0; i < item.Addresses.Length; i++)
            {
                writer.Encode(item.Addresses[i]);
            }
            writer.Encode(item.AreFinalized);
        }

        public override void PopulateLength(PendingValidators item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Add(1);
                return;
            }

            using LengthListWriter sequence = lengths.BeginContainer();
            sequence.Add(Rlp.LengthOf(item.BlockNumber)
                         + Rlp.LengthOf(item.BlockHash)
                         + Rlp.LengthOf(item.AreFinalized));
            using LengthListWriter addresses = sequence.BeginContainer();
            addresses.Add(GetAddressesLength(item.Addresses));
        }

        private static int GetAddressesLength(Address[] addresses)
        {
            const int AddressLengthWithRlpLengthPrefix = 1 + 20;

            return addresses.Length * AddressLengthWithRlpLengthPrefix;
        }
    }
}
