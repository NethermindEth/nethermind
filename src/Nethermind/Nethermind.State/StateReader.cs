// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
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

        public byte[] GetStorage(Keccak storageRoot, in UInt256 index)
        {
            if (storageRoot == Keccak.EmptyTreeHash)
            {
                return Bytes.ZeroByte;
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

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Keccak rootHash, VisitingOptions? visitingOptions = null)
        {
            _state.Accept(treeVisitor, rootHash, visitingOptions);
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
