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
// 

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

        public void Accept(ITreeVisitor visitor, Keccak stateRoot)
        {
            _stateReader.RunTreeVisitor(visitor,  stateRoot);
        }

        public bool AccountExists(Address address) => _stateReader.GetAccount(StateRoot, address) != null;

        public bool IsEmptyAccount(Address address) => GetAccount(address).IsEmpty;

        public bool IsDeadAccount(Address address)
        {
            Account account = GetAccount(address);
            return account.IsEmpty;
        }
    }
}
