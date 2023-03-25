// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie;

namespace Nethermind.Blockchain
{
    public class SpecificBlockReadOnlyStateProvider : IReadOnlyStateProvider
    {
        private readonly IStateReader _stateReader;

        public SpecificBlockReadOnlyStateProvider(IStateReader stateReader, Keccak? stateRoot = null)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            StateRoot = stateRoot ?? Keccak.EmptyTreeHash;
        }

        public virtual Keccak StateRoot { get; }

        public Account GetAccount(Address address) => _stateReader.GetAccount(StateRoot, address) ?? Account.TotallyEmpty;

        public UInt256 GetNonce(Address address) => GetAccount(address).Nonce;

        public UInt256 GetBalance(Address address) => GetAccount(address).Balance;

        public Keccak? GetStorageRoot(Address address) => GetAccount(address).StorageRoot;

        public byte[] GetCode(Address address) => _stateReader.GetCode(GetAccount(address).CodeHash);

        public byte[] GetCode(Keccak codeHash) => _stateReader.GetCode(codeHash);

        public Keccak GetCodeHash(Address address)
        {
            Account account = GetAccount(address);
            return account.CodeHash;
        }

        public void Accept(ITreeVisitor visitor, Keccak stateRoot, VisitingOptions? visitingOptions)
        {
            _stateReader.RunTreeVisitor(visitor, stateRoot, visitingOptions);
        }

        public bool AccountExists(Address address) => _stateReader.GetAccount(StateRoot, address) is not null;

        public bool IsEmptyAccount(Address address) => GetAccount(address).IsEmpty;

        public bool IsContract(Address address)
        {
            Account? account = GetAccount(address);
            if (account is null)
            {
                return false;
            }

            return account.IsContract;
        }

        public bool IsDeadAccount(Address address)
        {
            Account account = GetAccount(address);
            return account.IsEmpty;
        }
    }
}
