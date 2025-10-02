// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Account : IEquatable<Account>
    {
        public static readonly Account TotallyEmpty = new();

        private readonly AccountStruct _underlyingStuct;

        public Account(in UInt256 balance)
        {
            _underlyingStuct = new AccountStruct(balance);
        }

        public Account(in UInt256 nonce, in UInt256 balance)
        {
            _underlyingStuct = new AccountStruct(nonce, balance);
        }

        private Account()
        {
            _underlyingStuct = new AccountStruct();
        }

        public Account(in UInt256 nonce, in UInt256 balance, Hash256 storageRoot, Hash256 codeHash)
        {
            _underlyingStuct = new AccountStruct(nonce, balance, storageRoot, codeHash);
        }

        private Account(Account account, Hash256? storageRoot)
        {
            _underlyingStuct = new AccountStruct(
                account._underlyingStuct.Nonce,
                account._underlyingStuct.Balance,
                storageRoot,
                account._underlyingStuct.CodeHash
            );
        }

        private Account(Hash256? codeHash, Account account)
        {
            _underlyingStuct = new AccountStruct(
                account._underlyingStuct.Nonce,
                account._underlyingStuct.Balance,
                account._underlyingStuct.StorageRoot,
                codeHash
            );
        }

        private Account(Account account, in UInt256 nonce, in UInt256 balance)
        {
            _underlyingStuct = new AccountStruct(
                nonce,
                balance,
                account._underlyingStuct.StorageRoot,
                account._underlyingStuct.CodeHash
            );
        }

        public bool HasCode => _underlyingStuct.HasCode;
        public bool HasStorage => _underlyingStuct.HasStorage;
        public UInt256 Nonce => _underlyingStuct.Nonce;
        public ref readonly UInt256 Balance => ref _underlyingStuct.Balance;
        public Hash256 StorageRoot => _underlyingStuct.StorageRoot.ToCommitment();
        public Hash256 CodeHash => _underlyingStuct.CodeHash.ToCommitment();
        public bool IsTotallyEmpty => _underlyingStuct.IsTotallyEmpty;
        public bool IsEmpty => _underlyingStuct.IsEmpty;
        public bool IsContract => _underlyingStuct.IsContract;

        public bool Equals(Account? other)
        {
            if (other is null) return false;
            if (ReferenceEquals(this, other)) return true;

            return _underlyingStuct == other._underlyingStuct;
        }
        public override bool Equals(object? obj) => Equals(obj as Account);
        public static bool operator ==(Account? left, Account? right)
        {
            if (left is not null)
            {
                return left.Equals(right);
            }
            return right is null;
        }

        public override int GetHashCode() => _underlyingStuct.GetHashCode();
        public static bool operator !=(Account? left, Account? right) => !(left == right);

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

        public AccountStruct ToStruct() => new(Nonce, Balance, StorageRoot, CodeHash);
    }
}
