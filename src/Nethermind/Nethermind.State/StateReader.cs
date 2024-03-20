// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Metrics = Nethermind.Db.Metrics;
using EvmWord = System.Runtime.Intrinsics.Vector256<byte>;

namespace Nethermind.State
{
#pragma warning disable CS9113 // Parameter is unread.
    public class StateReader(IStateFactory factory, IKeyValueStore? codeDb, ILogManager? logManager) : IStateReader
#pragma warning restore CS9113 // Parameter is unread.
    {
        private readonly IKeyValueStore _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        private readonly IStateFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

        public bool TryGetAccount(Hash256 stateRoot, Address address, out AccountStruct account) => TryGetState(stateRoot, address, out account);

        public EvmWord GetStorage(Hash256 stateRoot, Address address, in UInt256 index)
        {
            if (!TryGetAccount(stateRoot, address, out AccountStruct account)) return default;

            ValueHash256 storageRoot = account.StorageRoot;
            if (storageRoot == Keccak.EmptyTreeHash)
            {
                return default;
            }
            Metrics.StorageTreeReads++;
            using IReadOnlyState state = GetReadOnlyState(stateRoot);
            return state.GetStorageAt(new StorageCell(address, index));
        }

        private IReadOnlyState GetReadOnlyState(Hash256 stateRoot) => _factory.GetReadOnly(stateRoot);

        public UInt256 GetBalance(Hash256 stateRoot, Address address)
        {
            TryGetState(stateRoot, address, out AccountStruct account);
            return account.Balance;
        }

        public byte[]? GetCode(Hash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];

        public void RunTreeVisitor(ITreeVisitor treeVisitor, Hash256 rootHash, VisitingOptions? visitingOptions = null)
        {
            throw new NotImplementedException($"The type of visitor {treeVisitor.GetType()} is not handled now");
        }

        public bool HasStateForRoot(Hash256 stateRoot) => _factory.HasRoot(stateRoot);

        public byte[]? GetCode(in ValueHash256 codeHash) => codeHash == Keccak.OfAnEmptyString ? Array.Empty<byte>() : _codeDb[codeHash.Bytes];

        private bool TryGetState(Hash256 stateRoot, Address address, out AccountStruct account)
        {
            if (stateRoot == Keccak.EmptyTreeHash)
            {
                account = AccountStruct.TotallyEmpty;
                return false;
            }

            Metrics.StateTreeReads++;

            using IReadOnlyState state = GetReadOnlyState(stateRoot);
            return state.TryGet(address, out account);
        }
    }
}
