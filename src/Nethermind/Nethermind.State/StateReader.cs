// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;
using Nethermind.Core.Threading;
using Metrics = Nethermind.Db.Metrics;
using EvmWord = System.Runtime.Intrinsics.Vector256<byte>;

namespace Nethermind.State
{
#pragma warning disable CS9113 // Parameter is unread.
    public class StateReader(IStateFactory factory, IKeyValueStore? codeDb, ILogManager? logManager) : IStateReader
#pragma warning restore CS9113 // Parameter is unread.
    {
        private readonly McsLock _lock = new();
        private readonly IKeyValueStore _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
        private readonly IStateFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        private CachedState? _cachedState;

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

            using var lockRelease = _lock.Acquire();

            return GetStateUnlocked(stateRoot)
                .GetStorageAt(new StorageCell(address, index));
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

            using var lockRelease = _lock.Acquire();

            return GetStateUnlocked(stateRoot)
                .TryGet(address, out account);
        }

        private IReadOnlyState GetStateUnlocked(Hash256 stateRoot)
        {
            CachedState? cachedState = _cachedState;
            if (cachedState is null || cachedState?.StateRoot != stateRoot || cachedState.IsDisposed)
            {
                cachedState?.Dispose();
                cachedState = _cachedState = new CachedState(stateRoot, GetReadOnlyState(stateRoot));
            }
            return cachedState.State;
        }

        private class CachedState(Hash256 stateRoot, IReadOnlyState state) : IDisposable
        {
            public readonly Hash256 StateRoot = stateRoot;
            public IReadOnlyState State = state;

            public bool IsDisposed => State is null;

            public void Dispose()
            {
                Interlocked.Exchange(ref State, null)?.Dispose();
            }
        }
    }
}
