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

using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;

namespace Nevermind.Core.Encoding
{
    public class AccountDecoder : IRlpDecoder<Account>
    {
        public Account Decode(Rlp rlp)
        {
            Account account = new Account();
            object[] data = (object[]) Rlp.Decode(rlp);
            account.Nonce = ((byte[])data[0]).ToUnsignedBigInteger();
            account.Balance = ((byte[]) data[1]).ToUnsignedBigInteger();
            account.StorageRoot = new Keccak((byte[])data[2]);
            account.CodeHash = new Keccak((byte[]) data[3]);
            return account;
        }
    }
}