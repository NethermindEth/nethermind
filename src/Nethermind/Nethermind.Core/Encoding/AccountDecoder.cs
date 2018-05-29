/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

namespace Nethermind.Core.Encoding
{
    public class AccountDecoder : IRlpDecoder<Account>
    {
        public Account Decode(Rlp.DecoderContext context, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            context.ReadSequenceLength();
            //long checkValue = context.ReadSequenceLength() + context.Position;

            Account account = new Account();
            account.Nonce = context.DecodeUBigInt();
            account.Balance = context.DecodeUBigInt();
            account.StorageRoot = context.DecodeKeccak();
            account.CodeHash = context.DecodeKeccak();

            //if (!rlpBehaviors.HasFlag(RlpBehaviors.AllowExtraData))
            //{
            //    context.Check(checkValue);
            //}

            return account;
        }

        public Rlp Encode(Account item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
                return Rlp.Encode(
                    Rlp.Encode(item.Nonce),
                    Rlp.Encode(item.Balance),
                    Rlp.Encode(item.StorageRoot),
                    Rlp.Encode(item.CodeHash));
        }
    }
}