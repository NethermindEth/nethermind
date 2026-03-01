// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal sealed class PendingValidatorsDecoder : RlpValueDecoder<PendingValidators>, IRlpObjectDecoder<PendingValidators>
    {
        protected override PendingValidators DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            System.Span<byte> span = rlpStream.PeekNextItem();
            Rlp.ValueDecoderContext ctx = new(span);
            PendingValidators result = DecodeInternal(ref ctx, rlpBehaviors);
            rlpStream.SkipItem();
            return result;
        }

        protected override PendingValidators DecodeInternal(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int sequenceLength = decoderContext.ReadSequenceLength();
            int pendingValidatorsCheck = decoderContext.Position + sequenceLength;

            long blockNumber = decoderContext.DecodeLong();
            var blockHash = decoderContext.DecodeKeccak();

            int addressSequenceLength = decoderContext.ReadSequenceLength();
            int addressCheck = decoderContext.Position + addressSequenceLength;
            List<Address> addresses = new List<Address>();
            while (decoderContext.Position < addressCheck)
            {
                addresses.Add(decoderContext.DecodeAddress());
            }
            decoderContext.Check(addressCheck);

            PendingValidators result = new PendingValidators(blockNumber, blockHash, addresses.ToArray())
            {
                AreFinalized = decoderContext.DecodeBool()
            };

            decoderContext.Check(pendingValidatorsCheck);

            return result;
        }

        public Rlp Encode(PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptyList;
            }

            RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data.ToArray());
        }

        public override void Encode(RlpStream rlpStream, PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(item.BlockNumber);
            rlpStream.Encode(item.BlockHash);
            rlpStream.StartSequence(addressesLength);
            for (int i = 0; i < item.Addresses.Length; i++)
            {
                rlpStream.Encode(item.Addresses[i]);
            }
            rlpStream.Encode(item.AreFinalized);
        }

        public override int GetLength(PendingValidators item, RlpBehaviors rlpBehaviors) =>
            item is null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

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
