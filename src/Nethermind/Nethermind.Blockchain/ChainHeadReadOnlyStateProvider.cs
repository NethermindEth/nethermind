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
    public class ChainHeadReadOnlyStateProvider : IReadOnlyStateProvider
    {
        private readonly IBlockTree _blockTree;
        private readonly IStateReader _stateReader;

        public ChainHeadReadOnlyStateProvider(IBlockTree blockTree, IStateReader stateReader)
        {
            _blockTree = blockTree ?? throw new ArgumentNullException(nameof(blockTree));
            _stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));
        }

        public Keccak StateRoot => _blockTree.Head?.StateRoot ?? Keccak.EmptyTreeHash;

        public Account GetAccount(Address address) => _stateReader.GetAccount(StateRoot, address);

        public UInt256 GetNonce(Address address) => _stateReader.GetNonce(StateRoot, address);

        public UInt256 GetBalance(Address address) => _stateReader.GetBalance(StateRoot, address);

        public Keccak GetStorageRoot(Address address) => _stateReader.GetStorageRoot(StateRoot, address);

        public byte[] GetCode(Address address) => _stateReader.GetCode(StateRoot, address);

        public byte[] GetCode(Keccak codeHash) => _stateReader.GetCode(codeHash);

        public Keccak GetCodeHash(Address address)
        {
            Account account = GetAccount(address);
            return account?.CodeHash ?? Keccak.OfAnEmptyString;
        }

        public void Accept(ITreeVisitor visitor, Keccak stateRoot)
        {
            _stateReader.RunTreeVisitor(visitor,  stateRoot);
        }

        public bool AccountExists(Address address) => GetAccount(address) != null;
        

        public bool IsEmptyAccount(Address address) => GetAccount(address).IsEmpty;

        public bool IsDeadAccount(Address address)
        {
            Account account = GetAccount(address);
            return account?.IsEmpty ?? true;
        }
    }
}
