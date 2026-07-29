// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Pbt;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt.Mirror;

/// <summary>Mirrors world-state reads and writes to PBT while retaining an authoritative backend.</summary>
/// <remarks>
/// Caller-visible values come from the authoritative backend; PBT disagreements throw
/// <see cref="PbtMirrorMismatchException"/>. PBT uses the authoritative root so both backends can
/// persist the same block ranges (see <see cref="PbtWorldStateScope.UseAuthoritativeRoot"/>).
/// </remarks>
public class PbtMirrorScopeProvider(
    IWorldStateScopeProvider authoritative,
    IPbtDbManager manager,
    IPbtResourcePool resourcePool,
    IPbtConfig config) : IWorldStateScopeProvider
{
    private static readonly ITrieWarmer _noopTrieWarmer = new NoopTrieWarmer();

    private readonly PbtTrieLayout _writeLayout = config.TrieNodeLayout;
    private readonly int _rootFoldConcurrency = config.RootFoldConcurrency;

    public bool HasRoot(BlockHeader? baseBlock) =>
        authoritative.HasRoot(baseBlock) && manager.HasStateForBlock(new StateId(baseBlock));

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
    {
        IWorldStateScopeProvider.IScope authoritativeScope = authoritative.BeginScope(baseBlock, metrics);
        try
        {
            StateId stateId = new(baseBlock);
            PbtWorldStateScope pbtScope = new(
                stateId,
                baseBlock,
                manager.GatherBundle(stateId, PbtResourcePool.Usage.MainBlockProcessing),
                authoritativeScope.CodeDb,
                manager,
                // The mirror supplies the root; do not consult the block tree.
                NullPbtChildHeaderSource.Instance,
                resourcePool,
                PbtResourcePool.Usage.MainBlockProcessing,
                isReadOnly: false,
                _writeLayout,
                _rootFoldConcurrency,
                _noopTrieWarmer);

            return new Scope(authoritativeScope, pbtScope);
        }
        catch
        {
            authoritativeScope.Dispose();
            throw;
        }
    }

    private sealed class Scope(IWorldStateScopeProvider.IScope authoritative, PbtWorldStateScope pbt) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<AddressAsKey, StorageTreeWrapper> _storages = [];

        public Hash256 RootHash => authoritative.RootHash;

        public void UpdateRootHash()
        {
            authoritative.UpdateRootHash();
            pbt.UseAuthoritativeRoot(authoritative.RootHash);
            pbt.UpdateRootHash();
        }

        public Account? Get(Address address)
        {
            Account? account = authoritative.Get(address);
            Account? mirrored = pbt.Get(address);
            if (!Matches(account, mirrored))
                throw new PbtMirrorMismatchException($"Account {address} differs: authoritative {account} vs pbt {mirrored}");

            return account;
        }

        /// <summary>Compares account existence and fields stored by PBT.</summary>
        /// <remarks>
        /// <see cref="Account.Equals(Account)"/> also compares the storage root, which EIP-8297
        /// accounts do not store because slots share one tree.
        /// </remarks>
        private static bool Matches(Account? authoritative, Account? mirrored) =>
            authoritative is null || mirrored is null
                ? authoritative is null && mirrored is null
                : authoritative.Nonce == mirrored.Nonce
                    && authoritative.Balance == mirrored.Balance
                    && authoritative.CodeHash == mirrored.CodeHash;

        public void HintGet(Address address, Account? account) => authoritative.HintGet(address, account);

        public void HintWarmAccount(in ValueAddress address) => authoritative.HintWarmAccount(in address);

        public void HintWarmSlot(in ValueAddress address, in UInt256 index) => authoritative.HintWarmSlot(in address, in index);

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) =>
            authoritative.HintBal(bal, sink);

        public IWorldStateScopeProvider.ICodeDb CodeDb => pbt.CodeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            ref StorageTreeWrapper? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (!exists)
                tree = new StorageTreeWrapper(authoritative.CreateStorageTree(address), pbt.CreateStorageTree(address), address);
            return tree!;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new WriteBatch(authoritative.StartWriteBatch(estimatedAccountNum), pbt.StartWriteBatch(estimatedAccountNum));

        public void Commit(ulong blockNumber)
        {
            authoritative.Commit(blockNumber);
            pbt.UseAuthoritativeRoot(authoritative.RootHash);
            pbt.Commit(blockNumber);
            _storages.Clear();
        }

        public void Dispose()
        {
            try
            {
                authoritative.Dispose();
            }
            finally
            {
                pbt.Dispose();
            }
        }
    }

    private sealed class StorageTreeWrapper(
        IWorldStateScopeProvider.IStorageTree authoritative,
        IWorldStateScopeProvider.IStorageTree pbt,
        Address address) : IWorldStateScopeProvider.IStorageTree
    {
        // PBT accounts have no per-account storage root.
        public Hash256 RootHash => authoritative.RootHash;

        public bool IsKnownEmpty => authoritative.IsKnownEmpty;

        public byte[] Get(in UInt256 index)
        {
            byte[] value = authoritative.Get(in index);
            byte[] mirrored = pbt.Get(in index);
            if (!Bytes.AreEqual(value, mirrored))
                throw new PbtMirrorMismatchException(
                    $"Slot {index} of {address} differs: authoritative {value.ToHexString(withZeroX: true)} vs pbt {mirrored.ToHexString(withZeroX: true)}");

            return value;
        }

        public void HintSet(in UInt256 index, byte[]? value) => authoritative.HintSet(in index, value);
    }

    /// <remarks>
    /// The authoritative batch emits accounts after folding dirty storage roots during
    /// <see cref="IDisposable.Dispose"/>. Forward those updates to keep the mirrored account tables aligned.
    /// </remarks>
    private sealed class WriteBatch : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly IWorldStateScopeProvider.IWorldStateWriteBatch _authoritative;
        private readonly IWorldStateScopeProvider.IWorldStateWriteBatch _pbt;

        public WriteBatch(
            IWorldStateScopeProvider.IWorldStateWriteBatch authoritative,
            IWorldStateScopeProvider.IWorldStateWriteBatch pbt)
        {
            _authoritative = authoritative;
            _pbt = pbt;
            _authoritative.OnAccountUpdated += OnAuthoritativeAccountUpdated;
        }

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account)
        {
            _authoritative.Set(key, account);
            _pbt.Set(key, account);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(
                _authoritative.CreateStorageWriteBatch(key, estimatedEntries),
                _pbt.CreateStorageWriteBatch(key, estimatedEntries));

        public void Dispose()
        {
            try
            {
                // Dispose this first to forward its account updates to the PBT batch.
                _authoritative.Dispose();
            }
            finally
            {
                _authoritative.OnAccountUpdated -= OnAuthoritativeAccountUpdated;
                _pbt.Dispose();
            }
        }

        private void OnAuthoritativeAccountUpdated(object? sender, IWorldStateScopeProvider.AccountUpdated updated)
        {
            _pbt.Set(updated.Address, updated.Account);
            OnAccountUpdated?.Invoke(sender, updated);
        }
    }

    private sealed class StorageWriteBatch(
        IWorldStateScopeProvider.IStorageWriteBatch authoritative,
        IWorldStateScopeProvider.IStorageWriteBatch pbt) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            authoritative.Set(in index, value);
            pbt.Set(in index, value);
        }

        public void Clear()
        {
            authoritative.Clear();
            pbt.Clear();
        }

        public void Dispose()
        {
            try
            {
                authoritative.Dispose();
            }
            finally
            {
                pbt.Dispose();
            }
        }
    }
}
