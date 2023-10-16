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

        public SpecificBlockReadOnlyStateProvider(IStateReader stateReader, Commitment? stateRoot = null)
        {
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
            StateRoot = stateRoot ?? Keccak.EmptyTreeHash;
        }

        public virtual Commitment StateRoot { get; }

        public Account GetAccount(Address address) => _stateReader.GetAccount(StateRoot, address) ?? Account.TotallyEmpty;

        public bool IsContract(Address address) => GetAccount(address).IsContract;

        public UInt256 GetNonce(Address address) => GetAccount(address).Nonce;

        public UInt256 GetBalance(Address address) => GetAccount(address).Balance;

        public Commitment? GetStorageRoot(Address address) => GetAccount(address).StorageRoot;

        public byte[] GetCode(Address address)
        {
            Account account = GetAccount(address);
            if (!account.HasCode)
            {
                return Array.Empty<byte>();
            }

            return _stateReader.GetCode(account.CodeHash);
        }

        public byte[] GetCode(Commitment codeHash) => _stateReader.GetCode(codeHash);

        public Commitment GetCodeHash(Address address)
        {
            Account account = GetAccount(address);
            return account.CodeHash;
        }

        public void Accept(ITreeVisitor visitor, Commitment stateRoot, VisitingOptions? visitingOptions)
        {
            _stateReader.RunTreeVisitor(visitor, stateRoot, visitingOptions);
        }

        public bool AccountExists(Address address) => _stateReader.GetAccount(StateRoot, address) is not null;

        public bool IsEmptyAccount(Address address) => GetAccount(address).IsEmpty;

        public bool IsDeadAccount(Address address)
        {
            Account account = GetAccount(address);
            return account.IsEmpty;
        }
    }
}
