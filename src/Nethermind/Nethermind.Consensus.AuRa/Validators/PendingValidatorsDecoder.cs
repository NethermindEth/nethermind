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

        public override void PopulateLength(PendingValidators item, ref LengthStack lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Push(1);
                return;
            }

            (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
            lengths.Push(addressesLength);
            lengths.Push(contentLength);
            lengths.Push(Rlp.LengthOfSequence(contentLength));
        }

        public override void Encode<TWriter>(PendingValidators item, ref LengthStack lengths, ref TWriter writer, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            lengths.Pop();
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            writer.StartSequence(lengths.Pop());
            writer.Encode(item.BlockNumber);
            writer.Encode(item.BlockHash);
            writer.StartSequence(lengths.Pop());
            for (int i = 0; i < item.Addresses.Length; i++)
            {
                writer.Encode(item.Addresses[i]);
            }
            writer.Encode(item.AreFinalized);
        }

        private static (int Total, int Addresses) GetContentLength(PendingValidators item, RlpBehaviors rlpBehaviors)
        {
            int contentLength = Rlp.LengthOf(item.BlockNumber)
                                + Rlp.LengthOf(item.BlockHash)
                                + Rlp.LengthOf(item.AreFinalized);

            int addressesLength = GetAddressesLength(item.Addresses);
            contentLength += Rlp.LengthOfSequence(addressesLength);

            return (contentLength, addressesLength);
        }

        private static int GetAddressesLength(Address[] addresses)
        {
            const int AddressLengthWithRlpLengthPrefix = 1 + 20;

            return addresses.Length * AddressLengthWithRlpLengthPrefix;
        }
    }
}
