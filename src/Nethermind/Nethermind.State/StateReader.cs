// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
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
    public class StateReader(IStateFactory stateFactory, IKeyValueStore? codeDb, ILogManager? logManager) : IStateReader
    {
        private readonly IStateFactory _factory = stateFactory ?? throw new ArgumentNullException(nameof(stateFactory));
        private readonly IKeyValueStore _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

        public bool TryGetAccount(Hash256 stateRoot, Address address, out AccountStruct account) => TryGetState(stateRoot, address, out account);

        public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, Address address, in UInt256 index)
        {
            Metrics.StorageReaderReads++;
            return _factory.GetStorage(stateRoot, address, index);
        }

        public UInt256 GetBalance(Hash256 stateRoot, Address address)
        {
            TryGetState(stateRoot, address, out AccountStruct account);
            return account.Balance;
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
        {
            throw new NotImplementedException($"The type of visitor {treeVisitor.GetType()} is not handled now");
        }

        public bool HasStateForRoot(Hash256 stateRoot) => _factory.HasRoot(stateRoot);

        public IScopedStateReader ForStateRoot(Hash256? stateRoot = null) =>
            new ScopedStateReader(_factory.GetReadOnly(stateRoot));

        public byte[]? GetCode(Hash256 stateRoot, Address address) =>
            TryGetState(stateRoot, address, out AccountStruct account) ? GetCode(account.CodeHash) : Array.Empty<byte>();

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];

        private bool TryGetState(Hash256 stateRoot, Address address, out AccountStruct account)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            Metrics.IncrementStateReaderReads();
            return _factory.TryGet(stateRoot, address, out account);
        }

        private class ScopedStateReader(IReadOnlyState state) : IScopedStateReader
        {
            public bool TryGetAccount(Address address, out AccountStruct account)
            {
                throw new NotImplementedException();
            }

            public ReadOnlySpan<byte> GetStorage(Address address, in UInt256 index) =>
                state.GetStorageAt(new StorageCell(address, index));

            public void Dispose() => state.Dispose();

            public Hash256 StateRoot => state.StateRoot;
        }
    }
}
