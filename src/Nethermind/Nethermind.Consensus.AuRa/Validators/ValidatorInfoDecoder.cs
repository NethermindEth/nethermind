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
            long finalizingBlockNumber = decoderContext.DecodeLong();
            long previousFinalizingBlockNumber = decoderContext.DecodeLong();

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

        public override void Encode<TWriter>(ref TWriter writer, ValidatorInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int contentLength, int validatorLength) = GetContentLength(item, rlpBehaviors);
            writer.StartSequence(contentLength);
            writer.Encode(item.FinalizingBlockNumber);
            writer.Encode(item.PreviousFinalizingBlockNumber);
            writer.StartSequence(validatorLength);
            for (int i = 0; i < item.Validators.Length; i++)
            {
                writer.Encode(item.Validators[i]);
            }
        }

        public override int GetLength(ValidatorInfo? item, RlpBehaviors rlpBehaviors) => item is null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

        private static (int Total, int Validators) GetContentLength(ValidatorInfo item, RlpBehaviors rlpBehaviors)
        {
            int validatorsLength = Rlp.LengthOfAddressRlp * item.Validators.Length;
            return (Rlp.LengthOf(item.FinalizingBlockNumber) + Rlp.LengthOf(item.PreviousFinalizingBlockNumber) + Rlp.LengthOfSequence(validatorsLength), validatorsLength);
        }
    }
}
