// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal sealed class ValidatorInfoDecoder : RlpDecoder<ValidatorInfo>
    {
        protected override ValidatorInfo? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemEmptyList())
            {
                decoderContext.ReadByte();
                return null;
            }

            int length = decoderContext.ReadSequenceLength();
            int check = decoderContext.Position + length;
            ulong finalizingBlockNumber = decoderContext.DecodeULong();
            ulong previousFinalizingBlockNumber = decoderContext.DecodeULong();

            int addressesSequenceLength = decoderContext.ReadSequenceLength();
            int addressesCheck = decoderContext.Position + addressesSequenceLength;
            int count = addressesSequenceLength / Rlp.LengthOfAddressRlp;
            decoderContext.GuardLimit(count);
            Address[] addresses = new Address[count];
            int i = 0;
            while (decoderContext.Position < addressesCheck)
            {
                addresses[i++] = decoderContext.DecodeAddress();
            }
            decoderContext.Check(addressesCheck);
            decoderContext.Check(check);

            return new ValidatorInfo(finalizingBlockNumber, previousFinalizingBlockNumber, addresses);
        }

        public override void Encode<TWriter>(ref TWriter writer, ValidatorInfo? item, ref LengthList lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            writer.StartSequence(lengths.Read());
            writer.Encode(item.FinalizingBlockNumber);
            writer.Encode(item.PreviousFinalizingBlockNumber);
            writer.StartSequence(lengths.Read());
            for (int i = 0; i < item.Validators.Length; i++)
            {
                writer.Encode(item.Validators[i]);
            }
        }

        public override void PopulateLength(ValidatorInfo? item, in LengthListWriter lengths, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                lengths.Add(1);
                return;
            }

            using LengthListWriter sequence = lengths.BeginContainer();
            sequence.Add(Rlp.LengthOf(item.FinalizingBlockNumber) + Rlp.LengthOf(item.PreviousFinalizingBlockNumber));
            using LengthListWriter validators = sequence.BeginContainer();
            validators.Add(Rlp.LengthOfAddressRlp * item.Validators.Length);
        }
    }
}
