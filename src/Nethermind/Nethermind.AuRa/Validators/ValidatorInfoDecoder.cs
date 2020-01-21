//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System.IO;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AuRa.Validators
{
    internal class ValidatorInfoDecoder : IRlpDecoder<ValidatorInfo>
    {
        public ValidatorInfo Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            var length = rlpStream.ReadSequenceLength();
            int check = rlpStream.Position + length;
            var finalizingBlockNumber = rlpStream.DecodeLong();
            var previousFinalizingBlockNumber= rlpStream.DecodeLong();

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

        public Rlp Encode(ValidatorInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new RlpStream(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, ValidatorInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item == null)
            {
                stream.EncodeNullObject();
                return;
            }

            var (contentLength, validatorLength)  = GetContentLength(item, rlpBehaviors);
            stream.StartSequence(contentLength);
            stream.Encode(item.FinalizingBlockNumber);
            stream.Encode(item.PreviousFinalizingBlockNumber);
            stream.StartSequence(validatorLength);
            for (int i = 0; i < item.Validators.Length; i++)
            {
                stream.Encode(item.Validators[i]);
            }
        }

        public void Encode(MemoryStream stream, ValidatorInfo item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            var (contentLength, validatorLength) = GetContentLength(item, rlpBehaviors);
            Rlp.StartSequence(stream, contentLength);
            Rlp.Encode(stream, item.FinalizingBlockNumber);
            Rlp.Encode(stream, item.PreviousFinalizingBlockNumber);
            Rlp.StartSequence(stream, validatorLength);
            for (int i = 0; i < item.Validators.Length; i++)
            {
                Rlp.Encode(stream, item.Validators[i]);
            }
        }

        public int GetLength(ValidatorInfo item, RlpBehaviors rlpBehaviors) => item == null ? 1 : Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors).Total);

        private static (int Total, int Validators) GetContentLength(ValidatorInfo item, RlpBehaviors rlpBehaviors)
        {
            var validatorsLength = Rlp.LengthOfAddressRlp * item.Validators.Length;
            return (Rlp.LengthOf(item.FinalizingBlockNumber) + Rlp.LengthOf(item.PreviousFinalizingBlockNumber) + Rlp.GetSequenceRlpLength(validatorsLength), validatorsLength);
        }
    }
} 