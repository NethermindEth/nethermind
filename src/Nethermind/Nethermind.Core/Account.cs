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

namespace Nethermind.Core
{
    public class Account
    {
        public static Account TotallyEmpty = new Account();

        public Account(BigInteger balance)
        {
            Balance = balance;
            Nonce = BigInteger.Zero;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.EmptyTreeHash;
            IsTotallyEmpty = Balance.IsZero;
        }

        private Account()
        {
            Balance = BigInteger.Zero;
            Nonce = BigInteger.Zero;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.EmptyTreeHash;
            IsTotallyEmpty = true;
        }

        public Account(BigInteger nonce, BigInteger balance, Keccak storageRoot, Keccak codeHash)
        {
            Nonce = nonce;
            Balance = balance;
            StorageRoot = storageRoot;
            CodeHash = codeHash;
            IsTotallyEmpty = Balance.IsZero && Nonce.IsZero && CodeHash == Keccak.OfAnEmptyString && StorageRoot == Keccak.EmptyTreeHash;
        }

        private Account(BigInteger nonce, BigInteger balance, Keccak storageRoot, Keccak codeHash, bool isTotallyEmpty)
        {
            Nonce = nonce;
            Balance = balance;
            StorageRoot = storageRoot;
            CodeHash = codeHash;
            IsTotallyEmpty = isTotallyEmpty;
        }

        public BigInteger Nonce { get; }
        public BigInteger Balance { get; }
        public Keccak StorageRoot { get; }
        public Keccak CodeHash { get; }
        public bool IsTotallyEmpty { get; } = false;
        public bool IsEmpty =>
            Balance.IsZero &&
            Nonce.IsZero &&
            CodeHash == Keccak.OfAnEmptyString;

        public Account WithChangedBalance(BigInteger newBalance)
        {
            return new Account(Nonce, newBalance, StorageRoot, CodeHash, IsTotallyEmpty && newBalance.IsZero);
        }

        public Account WithChangedNonce(BigInteger newNonce)
        {
            return new Account(newNonce, Balance, StorageRoot, CodeHash, IsTotallyEmpty && newNonce.IsZero);
        }

        public Account WithChangedStorageRoot(Keccak newStorageRoot)
        {
            return new Account(Nonce, Balance, newStorageRoot, CodeHash, IsTotallyEmpty && newStorageRoot == Keccak.EmptyTreeHash);
        }

        public Account WithChangedCodeHash(Keccak newCodeHash)
        {
            return new Account(Nonce, Balance, StorageRoot, newCodeHash, IsTotallyEmpty && newCodeHash == Keccak.OfAnEmptyString);
        }
    }
}