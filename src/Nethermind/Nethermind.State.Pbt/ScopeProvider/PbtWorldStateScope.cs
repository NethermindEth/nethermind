// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State.Pbt.ScopeProvider;

/// <summary>
/// The read/write surface of one processing branch: flat reads/writes go through the bundle,
/// while <see cref="UpdateRootHash"/> folds the block's cumulative dirty state into EIP-8297
/// tree keys, rewrites the touched stem leaf blobs, and runs a stem trie batch for the root.
/// </summary>
public sealed class PbtWorldStateScope : IWorldStateScopeProvider.IScope
{
    private readonly IPbtCommitTarget _commitTarget;
    private readonly bool _isReadOnly;

    // cumulative per-block dirty state; slot maps and destruct markers are concurrent because the
    // world state flushes storage write batches from parallel workers
    private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = [];
    private readonly ConcurrentDictionary<AddressAsKey, ConcurrentDictionary<UInt256, byte[]>> _dirtySlots = new();
    private readonly ConcurrentDictionary<AddressAsKey, bool> _selfDestructed = new();
    private readonly Dictionary<ValueHash256, byte[]> _pendingCode = [];
    private readonly Dictionary<AddressAsKey, PbtStorageTree> _storages = [];

    // results of the last UpdateRootHash, sealed into the snapshot at commit
    private readonly Dictionary<Stem, byte[]> _blobOverlay = [];
    private readonly Dictionary<TrieNodeKey, byte[]?> _nodeOverlay = [];

    private StateId _currentStateId;
    private Hash256 _rootHash;
    private bool _rootDirty;

    public PbtWorldStateScope(in StateId currentStateId, PbtSnapshotBundle bundle, IWorldStateScopeProvider.ICodeDb codeDb, IPbtCommitTarget commitTarget, bool isReadOnly)
    {
        _currentStateId = currentStateId;
        Bundle = bundle;
        _commitTarget = commitTarget;
        _isReadOnly = isReadOnly;
        _rootHash = currentStateId.StateRoot.ToHash256();
        CodeDb = new PbtCodeDb(codeDb, _pendingCode);
    }

    internal PbtSnapshotBundle Bundle { get; }

    public Hash256 RootHash => _rootHash;

    public IWorldStateScopeProvider.ICodeDb CodeDb { get; }

    public Account? Get(Address address) => Bundle.GetAccount(address);

    public void HintGet(Address address, Account? account)
    {
    }

    public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

    public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
    {
        ref PbtStorageTree? tree = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
        if (!exists) tree = new PbtStorageTree(this, address);
        return tree!;
    }

    public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => new WriteBatch(this);

    /// <summary>
    /// Recomputes the root from the block's cumulative dirty state. Idempotent: every call
    /// rebuilds the blob and node overlays from scratch against the pre-block state, so repeated
    /// calls (and calls interleaved with further writes) converge on the same result.
    /// </summary>
    public void UpdateRootHash()
    {
        if (!_rootDirty) return;

        _blobOverlay.Clear();
        _nodeOverlay.Clear();

        Dictionary<Stem, Dictionary<byte, byte[]?>> stemChanges = [];
        Dictionary<Stem, byte[]?> priorBlobs = [];
        BuildStemChanges(stemChanges, priorBlobs);

        Dictionary<Stem, ValueHash256?> stemRoots = new(stemChanges.Count);
        foreach ((Stem stem, Dictionary<byte, byte[]?> changes) in stemChanges)
        {
            byte[] blob = StemLeafBlob.Apply(GetPriorBlob(priorBlobs, stem) ?? [], changes);
            _blobOverlay[stem] = blob;
            if (blob.Length == 0)
            {
                // not a ternary: `cond ? null : valueHash256` silently routes the null through the
                // implicit Hash256? conversion and yields a zero hash instead of a null
                stemRoots[stem] = null;
            }
            else
            {
                stemRoots[stem] = StemLeafBlob.ComputeSubtreeRoot(blob);
            }
        }

        _rootHash = new StemTrie(Bundle).BatchUpdate(stemRoots, _nodeOverlay).ToHash256();
        _rootDirty = false;
    }

    public void Commit(ulong blockNumber)
    {
        UpdateRootHash();

        StateId newStateId = new(blockNumber, _rootHash);
        if (newStateId != _currentStateId)
        {
            PbtSnapshot snapshot = Bundle.CollectSnapshot(_currentStateId, newStateId, _blobOverlay, _nodeOverlay);
            if (_isReadOnly)
            {
                // read-only scopes keep the layer only in their own bundle; drop the lease that
                // would have gone to the repository
                snapshot.Dispose();
            }
            else
            {
                _commitTarget.AddSnapshot(snapshot);
            }

            _currentStateId = newStateId;
        }

        _dirtyAccounts.Clear();
        _dirtySlots.Clear();
        _selfDestructed.Clear();
        _pendingCode.Clear();
        _blobOverlay.Clear();
        _nodeOverlay.Clear();
        _storages.Clear();
        _rootDirty = false;
    }

    public void Dispose() => Bundle.Dispose();

    /// <summary>Derives the EIP-8297 leaf changes of the block: header stems from dirty accounts (with code mirrored as chunks), storage stems from dirty slots, and clears from self-destructs.</summary>
    private void BuildStemChanges(Dictionary<Stem, Dictionary<byte, byte[]?>> stemChanges, Dictionary<Stem, byte[]?> priorBlobs)
    {
        Dictionary<AddressAsKey, Stem> headerStems = [];

        // self-destructed (including deleted) accounts drop every prior header leaf: basic data,
        // code hash, header slots and header code chunks. Storage-zone stems of pre-existing
        // storage are left in place — clearing them would need a per-address stem index — which is
        // an accepted divergence from a from-scratch merkelization (flat reads stay correct via
        // the self-destruct markers).
        foreach ((AddressAsKey address, _) in _selfDestructed)
        {
            Stem headerStem = GetHeaderStem(headerStems, address);
            byte[]? prior = GetPriorBlob(priorBlobs, headerStem);
            if (prior is null) continue;

            Dictionary<byte, byte[]?> changes = GetStemChanges(stemChanges, headerStem);
            for (int subIndex = 0; subIndex < PbtKeyDerivation.StemSubtreeWidth; subIndex++)
            {
                if (StemLeafBlob.TryGetValue(prior, (byte)subIndex, out _)) changes[(byte)subIndex] = null;
            }
        }

        foreach ((AddressAsKey address, Account? account) in _dirtyAccounts)
        {
            // deletions were handled by the self-destruct pass
            if (account is null) continue;

            Stem headerStem = GetHeaderStem(headerStems, address);
            Dictionary<byte, byte[]?> changes = GetStemChanges(stemChanges, headerStem);

            byte[]? code = account.HasCode ? CodeDb.GetCode(account.CodeHash) : null;

            byte[] basicData = new byte[32];
            PbtKeyDerivation.PackBasicData(basicData, (uint)(code?.Length ?? 0), account.Nonce, account.Balance);
            changes[PbtKeyDerivation.BasicDataLeafKey] = basicData;
            changes[PbtKeyDerivation.CodeHashLeafKey] = account.CodeHash.BytesToArray();

            if (code is not null && CodeChunksNeeded(address, headerStem, account, priorBlobs))
            {
                byte[][] chunks = PbtKeyDerivation.ChunkifyCode(code);
                int headerChunks = Math.Min(chunks.Length, PbtKeyDerivation.HeaderCodeChunks);
                for (int i = 0; i < headerChunks; i++)
                {
                    changes[PbtKeyDerivation.HeaderCodeChunkSubIndex(i)] = chunks[i];
                }

                // overflow chunks are content-addressed by code hash and shared between contracts;
                // rewriting existing values is idempotent and they are never deleted
                for (int i = PbtKeyDerivation.HeaderCodeChunks; i < chunks.Length; i++)
                {
                    Stem overflowStem = PbtKeyDerivation.CodeOverflowStem(account.CodeHash, i, out byte subIndex);
                    GetStemChanges(stemChanges, overflowStem)[subIndex] = chunks[i];
                }
            }
        }

        foreach ((AddressAsKey address, ConcurrentDictionary<UInt256, byte[]> slots) in _dirtySlots)
        {
            // slots of an account that ends the block deleted never reach the tree
            if (_dirtyAccounts.TryGetValue(address, out Account? account) && account is null) continue;

            foreach ((UInt256 slot, byte[] value) in slots)
            {
                byte[]? value32 = AsLeafValue(value);
                if (PbtKeyDerivation.IsHeaderSlot(slot))
                {
                    GetStemChanges(stemChanges, GetHeaderStem(headerStems, address))[PbtKeyDerivation.HeaderSlotSubIndex(slot)] = value32;
                }
                else
                {
                    Stem stem = PbtKeyDerivation.StorageStem(address, slot, out byte subIndex);
                    GetStemChanges(stemChanges, stem)[subIndex] = value32;
                }
            }
        }
    }

    private bool CodeChunksNeeded(AddressAsKey address, in Stem headerStem, Account account, Dictionary<Stem, byte[]?> priorBlobs)
    {
        // a self-destruct in this block cleared the prior chunks, so they must be rewritten
        if (_selfDestructed.ContainsKey(address)) return true;

        byte[]? prior = GetPriorBlob(priorBlobs, headerStem);
        return prior is null
            || !StemLeafBlob.TryGetValue(prior, PbtKeyDerivation.CodeHashLeafKey, out ReadOnlySpan<byte> priorCodeHash)
            || !priorCodeHash.SequenceEqual(account.CodeHash.Bytes);
    }

    private Stem GetHeaderStem(Dictionary<AddressAsKey, Stem> cache, AddressAsKey address)
    {
        ref Stem stem = ref CollectionsMarshal.GetValueRefOrAddDefault(cache, address, out bool exists);
        if (!exists) stem = PbtKeyDerivation.AccountHeaderStem(address);
        return stem;
    }

    private byte[]? GetPriorBlob(Dictionary<Stem, byte[]?> cache, in Stem stem)
    {
        ref byte[]? blob = ref CollectionsMarshal.GetValueRefOrAddDefault(cache, stem, out bool exists);
        if (!exists) blob = Bundle.GetLeafBlob(stem);
        return blob;
    }

    private static Dictionary<byte, byte[]?> GetStemChanges(Dictionary<Stem, Dictionary<byte, byte[]?>> stemChanges, in Stem stem)
    {
        ref Dictionary<byte, byte[]?>? changes = ref CollectionsMarshal.GetValueRefOrAddDefault(stemChanges, stem, out _);
        return changes ??= [];
    }

    private static byte[]? AsLeafValue(byte[] value)
    {
        if (value.AsSpan().IsZero()) return null;
        if (value.Length == 32) return value;

        byte[] padded = new byte[32];
        value.CopyTo(padded.AsSpan(32 - value.Length));
        return padded;
    }

    private void SetSlot(Address address, in UInt256 slot, byte[] value)
    {
        Bundle.SetSlot(address, slot, value);
        _rootDirty = true;
    }

    private void SelfDestructStorage(Address address)
    {
        _selfDestructed[address] = true;
        _dirtySlots.TryRemove(address, out _);
        Bundle.SelfDestruct(address);
        _rootDirty = true;
    }

    private sealed class WriteBatch(PbtWorldStateScope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        // PBT accounts have no storage root to fold back into the state provider, so the event is never raised
        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated
        {
            add
            {
            }
            remove
            {
            }
        }

        public void Set(Address key, Account? account)
        {
            scope._dirtyAccounts[key] = account;
            scope.Bundle.SetAccount(key, account);
            scope._rootDirty = true;

            // the world state skips the storage write batch entirely for removed accounts, so the
            // storage clear must happen here
            if (account is null) scope.SelfDestructStorage(key);
        }

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries) =>
            new StorageWriteBatch(scope, key, scope._dirtySlots.GetOrAdd(key, static _ => new ConcurrentDictionary<UInt256, byte[]>()));

        public void Dispose()
        {
        }
    }

    private sealed class StorageWriteBatch(PbtWorldStateScope scope, Address address, ConcurrentDictionary<UInt256, byte[]> slots) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        public void Set(in UInt256 index, byte[] value)
        {
            slots[index] = value;
            scope.SetSlot(address, index, value);
        }

        public void Clear()
        {
            slots.Clear();
            scope.SelfDestructStorage(address);
            // reattach the (now empty) map so writes after the clear still register
            scope._dirtySlots[address] = slots;
        }

        public void Dispose()
        {
        }
    }
}
