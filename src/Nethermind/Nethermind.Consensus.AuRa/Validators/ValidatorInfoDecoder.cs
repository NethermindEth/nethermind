// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    internal class ValidatorInfoDecoder : IRlpStreamDecoder<ValidatorInfo>, IRlpObjectDecoder<ValidatorInfo>
    {
        public ValidatorInfo? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            var length = rlpStream.ReadSequenceLength();
            int check = rlpStream.Position + length;
            var finalizingBlockNumber = rlpStream.DecodeLong();
            var previousFinalizingBlockNumber = rlpStream.DecodeLong();

            int addressesSequenceLength = rlpStream.ReadSequenceLength();
            int addressesCheck = rlpStream.Position + addressesSequenceLength;
            Address[] addresses = new Address[addressesSequenceLength / Rlp.LengthOfAddressRlp];
            int i = 0;
            while (rlpStream.Position < addressesCheck)
            {
                addresses[i++] = rlpStream.DecodeAddress();
            }
            rlpStream.Check(addressesCheck);
            rlpStream.Check(check);

            return new ValidatorInfo(finalizingBlockNumber, previousFinalizingBlockNumber, addresses);
        }

        public Rlp Encode(ValidatorInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, ValidatorInfo? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                stream.EncodeNullObject();
                return;
            }

            var (contentLength, validatorLength) = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode(item.FinalizingBlockNumber);
            stream.Encode(item.PreviousFinalizingBlockNumber);
            stream.StartSequence(validatorLength);
            for (int i = 0; i < item.Validators.Length; i++)
            {
                stream.Encode(item.Validators[i]);
            }
        }

        public int GetLength(ValidatorInfo? item, RlpBehaviors rlpBehaviors) => item is null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

        private static (int Total, int Validators) GetContentLength(ValidatorInfo item, RlpBehaviors rlpBehaviors)
        {
            int validatorsLength = Rlp.LengthOfAddressRlp * item.Validators.Length;
            return (Rlp.LengthOf(item.FinalizingBlockNumber) + Rlp.LengthOf(item.PreviousFinalizingBlockNumber) + Rlp.LengthOfSequence(validatorsLength), validatorsLength);
        }
    }
}
