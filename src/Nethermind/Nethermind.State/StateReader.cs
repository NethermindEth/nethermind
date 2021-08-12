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

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    public class StateReader : IStateReader
    {
        private readonly IDb _codeDb;
        private readonly ILogger _logger;
        private readonly StateTree _state;
        private readonly StorageTree _storage;

        public StateReader(ITrieStore? trieStore, IDb? codeDb, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<StateReader>() ?? throw new ArgumentNullException(nameof(logManager));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _state = new StateTree(trieStore, logManager);
            _storage = new StorageTree(trieStore, Keccak.EmptyTreeHash, logManager);
        }

        public Account? GetAccount(Keccak stateRoot, Address address)
        {
            return GetState(stateRoot, address);
        }

        public byte[] GetStorage(Keccak storageRoot, UInt256 index)
        {
            if (storageRoot == Keccak.EmptyTreeHash)
            {
                return new byte[] {0};
            }

            Metrics.StorageTreeReads++;
            return _storage.Get(index, storageRoot);
        }

        public UInt256 GetBalance(Keccak stateRoot, Address address)
        {
            return GetState(stateRoot, address)?.Balance ?? UInt256.Zero;
        }

        public byte[]? GetCode(Keccak codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return Array.Empty<byte>();
            }

            return _codeDb[codeHash.Bytes];
        }

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak rootHash)
        {
            _state.Accept(treeVisitor, rootHash, treeVisitor.GetSupportedOptions());
        }

        public byte[] GetCode(Keccak stateRoot, Address address)
        {
            Account? account = GetState(stateRoot, address);
            return account is null ? Array.Empty<byte>() : GetCode(account.CodeHash);
        }

        private Account? GetState(Keccak stateRoot, Address address)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return null;
            }

            Metrics.StateTreeReads++;
            Account? account = _state.Get(address, stateRoot);
            return account;
        }
    }
}
