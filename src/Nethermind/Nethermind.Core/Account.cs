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

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;

namespace Nethermind.Core
{
    public class Account
    {
        public Account()
        {
            Balance = BigInteger.Zero;
            Nonce = BigInteger.Zero;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.EmptyTreeHash;
        }

        public BigInteger Nonce { get; set; }
        public BigInteger Balance { get; set; }
        public Keccak StorageRoot { get; set; }
        public Keccak CodeHash { get; set; }

        public bool IsSimple => CodeHash == Keccak.OfAnEmptyString;

        public bool IsEmpty =>
            Balance == BigInteger.Zero &&
            Nonce == BigInteger.Zero &&
            CodeHash == Keccak.OfAnEmptyString;

        public Account WithChangedBalance(BigInteger newBalance)
        {
            Account account = new Account();
            account.Nonce = Nonce;
            account.Balance = newBalance;
            account.StorageRoot = StorageRoot;
            account.CodeHash = CodeHash;
            return account;
        }

        public Account WithChangedNonce(BigInteger newNonce)
        {
            Account account = new Account();
            account.Nonce = newNonce;
            account.Balance = Balance;
            account.StorageRoot = StorageRoot;
            account.CodeHash = CodeHash;
            return account;
        }

        public Account WithChangedStorageRoot(Keccak newStorageRoot)
        {
            Account account = new Account();
            account.Nonce = Nonce;
            account.Balance = Balance;
            account.StorageRoot = newStorageRoot;
            account.CodeHash = CodeHash;
            return account;
        }

        public Account WithChangedCodeHash(Keccak newCodeHash)
        {
            Account account = new Account();
            account.Nonce = Nonce;
            account.Balance = Balance;
            account.StorageRoot = StorageRoot;
            account.CodeHash = newCodeHash;
            return account;
        }
    }
}