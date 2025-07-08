// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Metrics = Nethermind.Db.Metrics;

namespace Nethermind.State
{
    public class StateReader(ITrieStore trieStore, IKeyValueStore? codeDb, ILogManager? logManager) : IStateReader
    {
        private readonly IKeyValueStore _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        private readonly StateTree _state = new StateTree(trieStore.GetTrieStore(null), logManager);
        private readonly ITrieStore _trieStore = trieStore ?? throw new ArgumentNullException(nameof(trieStore));
        private readonly ILogManager _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account) => TryGetState(baseBlock, address, out account);

        public ReadOnlySpan<byte> GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index)
        {
            if (!TryGetAccount(baseBlock, address, out AccountStruct account)) return [];

            ValueHash256 storageRoot = account.StorageRoot;
            if (storageRoot == Keccak.EmptyTreeHash.ValueHash256)
            {
                return Bytes.ZeroByteSpan;
            }

            Metrics.StorageReaderReads++;

            StorageTree storage = new StorageTree(_trieStore.GetTrieStore(address), Keccak.EmptyTreeHash, _logManager);
            return storage.Get(index, new Hash256(storageRoot));
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? [] : _codeDb[codeHash.Bytes];

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, Hash256 stateRoot, VisitingOptions? visitingOptions = null) where TCtx : struct, INodeContext<TCtx>
        {
            _state.Accept(treeVisitor, stateRoot, visitingOptions);
        }

        public bool HasStateForBlock(BlockHeader? baseBlock) => trieStore.HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString.ValueHash256 ? [] : _codeDb[codeHash.Bytes];

        private bool TryGetState(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            Hash256 stateRoot = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            Metrics.IncrementStateReaderReads();
            return _state.TryGetStruct(address, out account, stateRoot);
        }
    }
}
