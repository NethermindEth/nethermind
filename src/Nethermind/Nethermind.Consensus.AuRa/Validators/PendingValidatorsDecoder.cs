// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal sealed class PendingValidatorsDecoder : RlpDecoder<PendingValidators>
    {
        protected override PendingValidators DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.TryReadEmptySequence())
            {
                return null;
            }

            RlpReader fields = decoderContext.EnterSequence(out int itemLength);

            ulong blockNumber = fields.DecodeULong();
            Hash256 blockHash = fields.DecodeKeccak();

            RlpReader addressReader = fields.EnterSequence(out int addressItemLength);
            List<Address> addresses = [];
            while (addressReader.HasNext)
            {
                addresses.Add(addressReader.DecodeAddress());
            }
            fields.ExitSequence(addressReader, addressItemLength);

            PendingValidators result = new(blockNumber, blockHash, addresses.ToArray())
            {
                AreFinalized = fields.DecodeBool()
            };

            decoderContext.ExitSequence(fields, itemLength);

            return result;
        }

        public override void Encode(ref RlpWriter writer, PendingValidators item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int contentLength, int addressesLength) = GetContentLength(item, rlpBehaviors);
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(item.BlockNumber);
            writer.Encode(item.BlockHash);
            RlpSequence addresses = writer.StartSequence(addressesLength);
            for (int i = 0; i < item.Addresses.Length; i++)
            {
                writer.Encode(item.Addresses[i]);
            }
            writer.EndSequence(addresses);
            writer.Encode(item.AreFinalized);
            writer.EndSequence(sequence);
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
