// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Account
    {
        public static readonly Account TotallyEmpty = new();

        private readonly Hash256? _codeHash;
        private readonly Hash256? _storageRoot;

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

        public Account(in UInt256 nonce, in UInt256 balance, Hash256 storageRoot, Hash256 codeHash)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public Account(in UInt256 nonce, in UInt256 balance, in UInt256 codeSize, in UInt256 version, Hash256 storageRoot, Hash256 codeHash)
        {
            _codeHash = codeHash == Keccak.OfAnEmptyString ? null : codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = codeSize;
            Version = version;
        }

        private Account(Account account, Hash256? storageRoot)
        {
            _codeHash = account._codeHash;
            _storageRoot = storageRoot == Keccak.EmptyTreeHash ? null : storageRoot;
            Nonce = account.Nonce;
            Balance = account.Balance;
            CodeSize = account.CodeSize;
            Version = account.Version;
        }

        private Account(Hash256? codeHash, Account account)
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
        public Hash256 StorageRoot => _storageRoot ?? Keccak.EmptyTreeHash;
        public Hash256 CodeHash => _codeHash ?? Keccak.OfAnEmptyString;
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

        public Account WithChangedStorageRoot(Hash256 newStorageRoot)
        {
            return new(this, newStorageRoot);
        }

        public Account WithChangedCodeHash(Hash256 newCodeHash, byte[]? code = null)
        {
            return new Account(newCodeHash, this) { Code = code, CodeSize = new UInt256((ulong)(code?.Length ?? 0)) };
        }

        public AccountStruct ToStruct() => new(Nonce, Balance, CodeSize, Version, StorageRoot, CodeHash);
    }

    public readonly struct AccountStruct
    {
        private readonly ValueHash256 _codeHash = Keccak.OfAnEmptyString.ValueHash256;
        private readonly ValueHash256 _storageRoot = Keccak.EmptyTreeHash.ValueHash256;

        public AccountStruct(in UInt256 nonce, in UInt256 balance, in ValueHash256 storageRoot, in ValueHash256 codeHash)
        {
            _codeHash = codeHash;
            _storageRoot = storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public AccountStruct(in UInt256 nonce, in UInt256 balance, in UInt256 codeSize, in UInt256 version, in ValueHash256 storageRoot, in ValueHash256 codeHash)
        {
            _codeHash = codeHash;
            _storageRoot = storageRoot;
            Nonce = nonce;
            Balance = balance;
            CodeSize = codeSize;
            Version = version;
        }

        public AccountStruct(in UInt256 nonce, in UInt256 balance)
        {
            Nonce = nonce;
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public AccountStruct(in UInt256 balance)
        {
            Balance = balance;
            CodeSize = 0;
            Version = 0;
        }

        public bool HasCode => _codeHash != Keccak.OfAnEmptyString.ValueHash256;

        public bool HasStorage => _storageRoot != Keccak.EmptyTreeHash.ValueHash256;

        public UInt256 Nonce { get; }
        public UInt256 Balance { get; }
        public UInt256 CodeSize { get; }
        public UInt256 Version { get; }
        public ValueHash256 StorageRoot => _storageRoot;
        public ValueHash256 CodeHash => _codeHash;
        public bool IsTotallyEmpty => _storageRoot == Keccak.EmptyTreeHash.ValueHash256 && IsEmpty;
        public bool IsEmpty => _codeHash == Keccak.OfAnEmptyString.ValueHash256 && Balance.IsZero && Nonce.IsZero;
        public bool IsContract => _codeHash != Keccak.OfAnEmptyString.ValueHash256;
    }
}
