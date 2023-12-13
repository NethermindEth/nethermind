// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Account
    {
        public static Account TotallyEmpty = new();

        private readonly Hash256? _codeHash;
        private readonly Hash256? _storageRoot;

        public Account(in UInt256 balance)
        {
            _codeHash = null;
            _storageRoot = null;
            Nonce = default;
            Balance = balance;
        }

        public Account(in UInt256 nonce, in UInt256 balance)
        {
            _codeHash = null;
            _storageRoot = null;
            Nonce = nonce;
            Balance = balance;
        }

        private Account()
        {
            _codeHash = null;
            _storageRoot = null;
            Nonce = default;
            Balance = default;
        }

        public Account(in UInt256 nonce, in UInt256 balance, Hash256 storageRoot, Hash256 codeHash)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = nonce;
            Balance = balance;
        }

        private Account(Account account, Hash256? storageRoot)
        {
            _codeHash = account._codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = account.Nonce;
            Balance = account.Balance;
        }

        private Account(Hash256? codeHash, Account account)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = account._storageRoot;
            Nonce = account.Nonce;
            Balance = account.Balance;
        }

        private Account(Account account, in UInt256 nonce, in UInt256 balance)
        {
            _codeHash = account._codeHash;
            _storageRoot = account._storageRoot;
            Nonce = nonce;
            Balance = balance;
        }

        public bool HasCode => _codeHash is not null;

        public bool HasStorage => _storageRoot is not null;

        public UInt256 Nonce { get; }
        public UInt256 Balance { get; }
        public Hash256 StorageRoot => _storageRoot ?? Keccak.EmptyTreeHash;
        public Hash256 CodeHash => _codeHash ?? Keccak.OfAnEmptyString;
        public bool IsTotallyEmpty => _storageRoot is null && IsEmpty;
        public bool IsEmpty => _codeHash is null && Balance.IsZero && Nonce.IsZero;
        public bool IsContract => _codeHash is not null;

        public Account WithChangedBalance(in UInt256 newBalance)
        {
            return new(this, Nonce, newBalance);
        }

        public Account WithChangedNonce(in UInt256 newNonce)
        {
            return new(this, newNonce, Balance);
        }

        public Account WithChangedStorageRoot(Hash256 newStorageRoot)
        {
            return new(this, newStorageRoot);
        }

        public Account WithChangedCodeHash(Hash256 newCodeHash)
        {
            return new(newCodeHash, this);
        }
    }
}
