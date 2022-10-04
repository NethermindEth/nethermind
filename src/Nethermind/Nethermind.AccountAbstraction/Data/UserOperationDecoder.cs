//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using Nethermind.AccountAbstraction.Network;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction.Data
{
    public class UserOperationDecoder : IRlpValueDecoder<UserOperationWithEntryPoint>, IRlpStreamDecoder<UserOperationWithEntryPoint>
    {
        public Rlp Encode(UserOperationWithEntryPoint? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data!);

        }

        public void Encode(RlpStream stream, UserOperationWithEntryPoint? opWithEntryPoint, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (opWithEntryPoint is null)
            {
                stream.EncodeNullObject();
                return;
            }

            int contentLength = GetContentLength(opWithEntryPoint);

            UserOperation op = opWithEntryPoint.UserOperation;
            Address entryPoint = opWithEntryPoint.EntryPoint;

            stream.StartSequence(contentLength);

            stream.Encode(op.Sender);
            stream.Encode(op.Nonce);
            stream.Encode(op.InitCode);
            stream.Encode(op.CallData);
            stream.Encode(op.CallGas);
            stream.Encode(op.VerificationGas);
            stream.Encode(op.PreVerificationGas);
            stream.Encode(op.MaxFeePerGas);
            stream.Encode(op.MaxPriorityFeePerGas);
            stream.Encode(op.Paymaster);
            stream.Encode(op.PaymasterData);
            stream.Encode(op.Signature);
            stream.Encode(entryPoint);
        }


        public UserOperationWithEntryPoint Decode(ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            throw new System.NotImplementedException();
        }

        public UserOperationWithEntryPoint Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            rlpStream.SkipLength();

            UserOperationRpc userOperationRpc = new UserOperationRpc
            {
                Sender = rlpStream.DecodeAddress() ?? Address.Zero,
                Nonce = rlpStream.DecodeUInt256(),
                InitCode = rlpStream.DecodeByteArray(),
                CallData = rlpStream.DecodeByteArray(),
                CallGas = rlpStream.DecodeUInt256(),
                VerificationGas = rlpStream.DecodeUInt256(),
                PreVerificationGas = rlpStream.DecodeUInt256(),
                MaxFeePerGas = rlpStream.DecodeUInt256(),
                MaxPriorityFeePerGas = rlpStream.DecodeUInt256(),
                Paymaster = rlpStream.DecodeAddress() ?? Address.Zero,
                PaymasterData = rlpStream.DecodeByteArray(),
                Signature = rlpStream.DecodeByteArray()
            };

            Address entryPoint = rlpStream.DecodeAddress() ?? Address.Zero;

            // TODO: Make instantiation simpler?
            return new UserOperationWithEntryPoint(new UserOperation(userOperationRpc), entryPoint);
        }

        public int GetLength(UserOperationWithEntryPoint item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item));
        }

        private static int GetContentLength(UserOperationWithEntryPoint opWithEntryPoint)
        {
            UserOperation op = opWithEntryPoint.UserOperation;
            Address entryPoint = opWithEntryPoint.EntryPoint;

            return Rlp.LengthOf(op.Sender)
                   + Rlp.LengthOf(op.Nonce)
                   + Rlp.LengthOf(op.InitCode)
                   + Rlp.LengthOf(op.CallData)
                   + Rlp.LengthOf(op.CallGas)
                   + Rlp.LengthOf(op.VerificationGas)
                   + Rlp.LengthOf(op.PreVerificationGas)
                   + Rlp.LengthOf(op.MaxFeePerGas)
                   + Rlp.LengthOf(op.MaxPriorityFeePerGas)
                   + Rlp.LengthOf(op.Paymaster)
                   + Rlp.LengthOf(op.PaymasterData)
                   + Rlp.LengthOf(op.Signature)
                   + Rlp.LengthOf(entryPoint);
        }

    }
}
