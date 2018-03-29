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
        public Account Decode(Rlp rlp)
        {
            DecodedRlp data = Rlp.Decode(rlp);
            return Decode(data);
        }

        public Account Decode(DecodedRlp data)
        {
            Account account = new Account();
            account.Nonce = data.GetUnsignedBigInteger(0);
            account.Balance = data.GetUnsignedBigInteger(1);
            account.StorageRoot = data.GetKeccak(2);
            account.CodeHash = data.GetKeccak(3);
            return account;
        }
    }
}