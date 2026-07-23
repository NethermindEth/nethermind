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
using Nethermind.State.Pbt.ScopeProvider;

namespace Nethermind.State.Pbt.Mirror;

/// <summary>
/// Drives a PBT state alongside an authoritative backend over the very same block processing: every
/// read is served by both and compared, and every write is applied to both.
/// </summary>
/// <remarks>
/// The authoritative backend owns everything caller-visible — the reported root hash, the storage
/// roots, the values handed back to the EVM — so that a mirrored node processes blocks exactly as an
/// unmirrored one does; PBT is a shadow whose only observable effect is a
/// <see cref="PbtMirrorMismatchException"/> when it disagrees. Nothing here is specific to the flat
/// backend: it wraps whichever provider it decorates.
/// <para>
/// PBT states are keyed by the authoritative root rather than by the root the tree folds to (see
/// <see cref="PbtWorldStateScope.UseAuthoritativeRoot"/>), which is what lets the two backends persist
/// the same block ranges. Their code databases are the same store, so the scope's single code db is
/// PBT's — it writes through the authoritative one, and additionally captures the bytes PBT needs to
/// chunk into the tree.
/// </para>
/// </remarks>
public class PbtMirrorScopeProvider(
    IWorldStateScopeProvider authoritative,
    IPbtDbManager manager,
    IPbtResourcePool resourcePool,
    IPbtConfig config) : IWorldStateScopeProvider
{
    private readonly PbtTrieFormat _writeFormat = config.TrieNodeWriteFormat();
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
                // the mirror supplies the root itself, so the block tree is never consulted for one
                NullPbtChildHeaderSource.Instance,
                resourcePool,
                PbtResourcePool.Usage.MainBlockProcessing,
                isReadOnly: false,
                _writeFormat,
                _rootFoldConcurrency);

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
            if (mirrored != account)
                throw new PbtMirrorMismatchException($"Account {address} differs: authoritative {account} vs pbt {mirrored}");

            return account;
        }

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
        // PBT accounts have no per-account storage root, so only the authoritative one is meaningful
        public Hash256 RootHash => authoritative.RootHash;

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
    /// The authoritative batch is the one that reports account updates, and it reports them from its
    /// own <see cref="IDisposable.Dispose"/> — where it folds each dirty storage tree's new root into
    /// the account. Those patched accounts are the ones that end up stored, so they are forwarded into
    /// the PBT batch as they are raised; without that the two account tables would part ways on the
    /// storage root and the very next <see cref="Scope.Get"/> would report a mismatch.
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
                // first: its dispose is what raises the account updates the PBT batch still needs
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
