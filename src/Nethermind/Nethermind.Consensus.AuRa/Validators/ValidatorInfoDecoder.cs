// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal sealed class ValidatorInfoDecoder : RlpDecoder<ValidatorInfo>
    {
        protected override ValidatorInfo? DecodeInternal(ref RlpReader decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.TryReadEmptySequence())
            {
                return null;
            }

            RlpReader fields = decoderContext.EnterSequence(out int itemLength);
            ulong finalizingBlockNumber = fields.DecodeULong();
            ulong previousFinalizingBlockNumber = fields.DecodeULong();

            RlpReader addressesReader = fields.EnterSequence(out int addressesItemLength);
            Address[] addresses = new Address[addressesReader.CountItems(RlpLimit.DefaultLimit)];
            for (int i = 0; i < addresses.Length; i++)
            {
                addresses[i] = addressesReader.DecodeAddress();
            }
            fields.ExitSequence(addressesReader, addressesItemLength);
            decoderContext.ExitSequence(fields, itemLength);

            return new ValidatorInfo(finalizingBlockNumber, previousFinalizingBlockNumber, addresses);
        }

        public override void Encode(ref RlpWriter writer, ValidatorInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                writer.EncodeNullObject();
                return;
            }

            (int contentLength, int validatorLength) = GetContentLength(item, rlpBehaviors);
            RlpSequence sequence = writer.StartSequence(contentLength);
            writer.Encode(item.FinalizingBlockNumber);
            writer.Encode(item.PreviousFinalizingBlockNumber);
            RlpSequence validators = writer.StartSequence(validatorLength);
            for (int i = 0; i < item.Validators.Length; i++)
            {
                writer.Encode(item.Validators[i]);
            }
            writer.EndSequence(validators);
            writer.EndSequence(sequence);
        }

        public override int GetLength(ValidatorInfo? item, RlpBehaviors rlpBehaviors) => item is null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

        private static (int Total, int Validators) GetContentLength(ValidatorInfo item, RlpBehaviors rlpBehaviors)
        {
            int validatorsLength = Rlp.LengthOfAddressRlp * item.Validators.Length;
            return (Rlp.LengthOf(item.FinalizingBlockNumber) + Rlp.LengthOf(item.PreviousFinalizingBlockNumber) + Rlp.LengthOfSequence(validatorsLength), validatorsLength);
        }
    }
}
