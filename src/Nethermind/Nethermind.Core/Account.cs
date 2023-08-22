// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Account
    {
        public static Account TotallyEmpty = new();

        private readonly Keccak? _codeHash;
        private readonly Keccak? _storageRoot;

        public Account(in UInt256 balance)
        {
            _codeHash = null;
            _storageRoot = null;
            Nonce = default;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public Account(in UInt256 nonce, in UInt256 balance)
        {
            _codeHash = null;
            _storageRoot = null;
            Nonce = nonce;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        private Account()
        {
            _codeHash = null;
            _storageRoot = null;
            Nonce = default;
            Balance = default;
            CodeSize = 0;
            Version = 0;
        }

        public Account(in UInt256 nonce, in UInt256 balance, Keccak storageRoot, Keccak codeHash)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public Account(in UInt256 nonce, in UInt256 balance, in UInt256 codeSize, in UInt256 version, Keccak storageRoot, Keccak codeHash)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = codeSize;
            Version = version;
        }

        private Account(Account account, Keccak? storageRoot)
        {
            _codeHash = account._codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = account.Nonce;
            Balance = account.Balance;
            CodeSize = account.CodeSize;
            Version = account.Version;
        }

        private Account(Keccak? codeHash, Account account)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = account._storageRoot;
            Nonce = account.Nonce;
            Balance = account.Balance;
            CodeSize = account.CodeSize;
            Version = account.Version;
        }

        private Account(Account account, in UInt256 nonce, in UInt256 balance)
        {
            _codeHash = account._codeHash;
            _storageRoot = account._storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public bool HasCode => _codeHash is not null;

        public bool HasStorage => _storageRoot is not null;

        public UInt256 Nonce { get; }
        public UInt256 Balance { get; }
        public UInt256 CodeSize { get; set; }
        public UInt256 Version { get; }
        public Keccak StorageRoot => _storageRoot ?? Keccak.EmptyTreeHash;
        public Keccak CodeHash => _codeHash ?? Keccak.OfAnEmptyString;
        public bool IsTotallyEmpty => _storageRoot is null && IsEmpty;
        public bool IsEmpty => _codeHash is null && Balance.IsZero && Nonce.IsZero;
        public bool IsContract => _codeHash is not null;

        public byte[]? Code { get; set; }

        public Account WithChangedBalance(in UInt256 newBalance)
        {
            return new(this, Nonce, newBalance);
        }

        public Account WithChangedNonce(in UInt256 newNonce)
        {
            return new(this, newNonce, Balance);
        }

        public Account WithChangedStorageRoot(Keccak newStorageRoot)
        {
            return new(this, newStorageRoot);
        }

        public Account WithChangedCodeHash(Keccak newCodeHash, byte[]? code = null)
        {
            return new(newCodeHash, this) { Code = code, CodeSize = new UInt256((ulong)(code?.Length ?? 0)) };
        }
    }
}
