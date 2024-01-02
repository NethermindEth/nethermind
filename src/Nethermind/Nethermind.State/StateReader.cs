// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly IStateFactory _factory;
        private readonly IKeyValueStore _codeDb;
        private readonly ILogger _logger;

        public StateReader(IStateFactory factory, IKeyValueStore? codeDb, ILogManager? logManager)
        {
            _logger = logManager?.GetClassLogger<StateReader>() ?? throw new ArgumentNullException(nameof(logManager));
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _factory = factory;
        }

        public Account? GetAccount(Hash256 stateRoot, Address address)
        {
            return GetState(stateRoot, address);
        }

        public byte[]? GetStorage(Hash256 stateRoot, Address address, in UInt256 index)
        {
            Metrics.StorageTreeReads++;
            using IReadOnlyState state = GetReadOnlyState(stateRoot);
            return state.GetStorageAt(new StorageCell(address, index));
        }

        private IReadOnlyState GetReadOnlyState(Hash256 stateRoot) => _factory.GetReadOnly(stateRoot);

        public UInt256 GetBalance(Hash256 stateRoot, Address address)
        {
            return GetState(stateRoot, address)?.Balance ?? UInt256.Zero;
        }

        public byte[]? GetCode(Hash256 codeHash)
        {
            if (codeHash == Keccak.OfAnEmptyString)
            {
                return Array.Empty<byte>();
            }

            return _codeDb[codeHash.Bytes];
        }

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
        {
            if (treeVisitor is RootCheckVisitor rootCheck)
            {
                rootCheck.HasRoot = _factory.HasRoot(rootHash);
                return;
            }

            throw new NotImplementedException($"The type of visitor {treeVisitor.GetType()} is not handled now");
        }

        public bool HasStateForRoot(Hash256 stateRoot)
        {
            RootCheckVisitor visitor = new();
            RunTreeVisitor(visitor, stateRoot);
            return visitor.HasRoot;
        }

        public byte[] GetCode(Hash256 stateRoot, Address address)
        {
            Account? account = GetState(stateRoot, address);
            return account is null ? Array.Empty<byte>() : GetCode(account.CodeHash);
        }

        private Account? GetState(Hash256 stateRoot, Address address)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                return null;
            }

            Metrics.StateTreeReads++;
            using IReadOnlyState state = GetReadOnlyState(stateRoot);
            return state.Get(address);
        }
    }
}
