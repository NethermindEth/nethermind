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

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Account
    {
        public static Account TotallyEmpty = new();

        private static UInt256 _accountStartNonce = UInt256.Zero;
        
        /// <summary>
        /// This is a special field that was used by some of the testnets (namely - Morden and Mordor).
        /// It makes all the account nonces start from a different number then zero,
        /// hence preventing potential signature reuse.
        /// It is no longer needed since the replay attack protection on chain ID is used now.
        /// We can remove it now but then we also need to remove any historical Mordor / Morden tests.
        /// </summary>
        public static UInt256 AccountStartNonce
        {
            set
            {
                _accountStartNonce = value;
                TotallyEmpty = new Account();
            }
        } 

        public Account(UInt256 balance)
        {
            Balance = balance;
            Nonce = _accountStartNonce;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.EmptyTreeHash;
            IsTotallyEmpty = Balance.IsZero;
        }

        private Account()
        {
            Balance = UInt256.Zero;
            Nonce = _accountStartNonce;
            CodeHash = Keccak.OfAnEmptyString;
            StorageRoot = Keccak.EmptyTreeHash;
            IsTotallyEmpty = true;
        }

        public Account(UInt256 nonce, UInt256 balance, Keccak storageRoot, Keccak codeHash)
        {
            Nonce = nonce;
            Balance = balance;
            StorageRoot = storageRoot;
            CodeHash = codeHash;
            IsTotallyEmpty = Balance.IsZero && Nonce == _accountStartNonce && CodeHash == Keccak.OfAnEmptyString && StorageRoot == Keccak.EmptyTreeHash;
        }

        private Account(UInt256 nonce, UInt256 balance, Keccak storageRoot, Keccak codeHash, bool isTotallyEmpty)
        {
            Nonce = nonce;
            Balance = balance;
            StorageRoot = storageRoot;
            CodeHash = codeHash;
            IsTotallyEmpty = isTotallyEmpty;
        }

        public bool HasCode => !CodeHash.Equals(Keccak.OfAnEmptyString);
        
        public bool HasStorage => !StorageRoot.Equals(Keccak.EmptyTreeHash);
        
        public UInt256 Nonce { get; }
        public UInt256 Balance { get; }
        public Keccak StorageRoot { get; }
        public Keccak CodeHash { get; }
        public bool IsTotallyEmpty { get; }
        public bool IsEmpty => IsTotallyEmpty || (Balance.IsZero && Nonce == _accountStartNonce && CodeHash == Keccak.OfAnEmptyString);
        public bool IsContract => CodeHash != Keccak.OfAnEmptyString; 

        public Account WithChangedBalance(UInt256 newBalance)
        {
            return new(Nonce, newBalance, StorageRoot, CodeHash, IsTotallyEmpty && newBalance.IsZero);
        }

        public Account WithChangedNonce(UInt256 newNonce)
        {
            return new(newNonce, Balance, StorageRoot, CodeHash, IsTotallyEmpty && newNonce == _accountStartNonce);
        }

        public Account WithChangedStorageRoot(Keccak newStorageRoot)
        {
            return new(Nonce, Balance, newStorageRoot, CodeHash, IsTotallyEmpty && newStorageRoot == Keccak.EmptyTreeHash);
        }

        public Account WithChangedCodeHash(Keccak newCodeHash)
        {
            return new(Nonce, Balance, StorageRoot, newCodeHash, IsTotallyEmpty && newCodeHash == Keccak.OfAnEmptyString);
        }
    }
}
