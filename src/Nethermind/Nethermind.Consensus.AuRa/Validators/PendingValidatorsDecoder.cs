// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal sealed class PendingValidatorsDecoder : RlpStreamDecoder<PendingValidators>, IRlpObjectDecoder<PendingValidators>
    {
        protected override PendingValidators DecodeInternal(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            var sequenceLength = rlpStream.ReadSequenceLength();
            var pendingValidatorsCheck = rlpStream.Position + sequenceLength;

            var blockNumber = rlpStream.DecodeLong();
            var blockHash = rlpStream.DecodeKeccak();

            var addressSequenceLength = rlpStream.ReadSequenceLength();
            var addressCheck = rlpStream.Position + addressSequenceLength;
            List<Address> addresses = new List<Address>();
            while (rlpStream.Position < addressCheck)
            {
                addresses.Add(rlpStream.DecodeAddress());
            }
            rlpStream.Check(addressCheck);

            var result = new PendingValidators(blockNumber, blockHash, addresses.ToArray())
            {
                AreFinalized = rlpStream.DecodeBool()
            };

            rlpStream.Check(pendingValidatorsCheck);

            return result;
        }

        public Rlp Encode(PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
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

            var addressesLength = GetAddressesLength(item.Addresses);
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
