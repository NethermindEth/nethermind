// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core
{
    public class Account
    {
        public readonly static Account TotallyEmpty = new();

        private Account()
        {
            _nonce = default;
            _balance = default;
        }

        public Account(in UInt256 balance)
        {
            _nonce = default;
            _balance = balance;
        }

        public Account(in UInt256 nonce, in UInt256 balance)
        {
            _nonce = nonce;
            _balance = balance;
        }

        protected readonly UInt256 _nonce;
        public UInt256 Nonce => _nonce;
        protected readonly UInt256 _balance;
        public UInt256 Balance => _balance;

        public virtual bool HasCode => false;
        public virtual bool HasStorage => false;
        public virtual Keccak StorageRoot => Keccak.EmptyTreeHash;
        public virtual Keccak CodeHash => Keccak.OfAnEmptyString;
        public virtual bool IsTotallyEmpty => IsEmpty;
        public virtual bool IsEmpty => Balance.IsZero && Nonce.IsZero;
        public virtual bool IsContract => false;

        public static Account CreateAccount(in UInt256 nonce, in UInt256 balance, Keccak storageRoot, Keccak codeHash)
        {
            if (storageRoot == Keccak.EmptyTreeHash)
            {
                if (codeHash == Keccak.OfAnEmptyString)
                {
                    return new Account(in nonce, in balance);
                }

                return new ContractAccount(in nonce, in balance, codeHash: codeHash);
            }

            if (codeHash == Keccak.OfAnEmptyString)
            {
                return new ContractAccount(storageRoot: storageRoot, in nonce, in balance);
            }

            return new ContractAccount(in nonce, in balance, storageRoot, codeHash);
        }

        public virtual Account WithChangedBalance(in UInt256 newBalance)
        {
            return new(in _nonce, in newBalance);
        }

        public virtual Account WithChangedNonce(in UInt256 newNonce)
        {
            return new(in newNonce, in _balance);
        }

        public virtual Account WithChangedStorageRoot(Keccak newStorageRoot)
        {
            if (newStorageRoot == Keccak.EmptyTreeHash)
            {
                return this;
            }
            else
            {
                return new ContractAccount(storageRoot: newStorageRoot, in _nonce, in _balance);
            }
        }

        public virtual Account WithChangedCodeHash(Keccak newCodeHash)
        {
            if (newCodeHash == Keccak.OfAnEmptyString)
            {
                return this;
            }
            else
            {
                return new ContractAccount(_nonce, _balance, codeHash: newCodeHash);
            }
        }
    }

    public sealed class ContractAccount : Account
    {
        public ContractAccount(in UInt256 nonce, in UInt256 balance, Keccak storageRoot, Keccak codeHash)
            : base(in nonce, in balance)
        {
            _storageRoot = storageRoot;
            _codeHash = codeHash;
        }

        public ContractAccount(Keccak storageRoot, in UInt256 nonce, in UInt256 balance)
            : base(in nonce, in balance)
        {
            _storageRoot = storageRoot;
            _codeHash = Keccak.OfAnEmptyString;
        }

        public ContractAccount(in UInt256 nonce, in UInt256 balance, Keccak codeHash)
            : base(in nonce, in balance)
        {
            _storageRoot = Keccak.EmptyTreeHash;
            _codeHash = codeHash;
        }

        private readonly Keccak _storageRoot;
        public override Keccak StorageRoot => _storageRoot;

        private readonly Keccak _codeHash;
        public override Keccak CodeHash => _codeHash;

        public override bool HasCode => !ReferenceEquals(_codeHash, Keccak.OfAnEmptyString);
        public override bool HasStorage => !ReferenceEquals(_storageRoot, Keccak.EmptyTreeHash);
        public override bool IsEmpty => !HasCode && base.IsEmpty;
        public override bool IsTotallyEmpty => false;
        public override bool IsContract => HasCode;

        public override ContractAccount WithChangedBalance(in UInt256 newBalance)
        {
            return new ContractAccount(in _nonce, in newBalance, _storageRoot, _codeHash);
        }

        public override ContractAccount WithChangedNonce(in UInt256 newNonce)
        {
            return new ContractAccount(in newNonce, in _balance, _storageRoot, _codeHash);
        }

        public override ContractAccount WithChangedStorageRoot(Keccak newStorageRoot)
        {
            if (!ReferenceEquals(newStorageRoot, Keccak.EmptyTreeHash)
                && newStorageRoot == Keccak.EmptyTreeHash)
            {
                newStorageRoot = Keccak.EmptyTreeHash;
            }

            return new ContractAccount(in _nonce, in _balance, newStorageRoot, _codeHash);
        }

        public override ContractAccount WithChangedCodeHash(Keccak newCodeHash)
        {
            if (!ReferenceEquals(newCodeHash, Keccak.OfAnEmptyString)
                && newCodeHash == Keccak.OfAnEmptyString)
            {
                newCodeHash = Keccak.OfAnEmptyString;
            }

            return new ContractAccount(in _nonce, in _balance, _storageRoot, newCodeHash);
        }
    }
}
