// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie;
using Nethermind.Zkvm.Abstractions;

namespace Nethermind.State;

/// <summary>World-state scopes over the tries libairbender keeps of the witness nodes it read.</summary>
/// <remarks>
/// Airbender only. The state and storage tries live in libairbender (its <c>mpt.c</c>): a read walks the witness
/// encodings where they lie, a write builds the nodes on its path, and a root is hashed once, in native code. What
/// stays here is what <see cref="TrieStoreScopeProvider"/> does around its trees: the accounts' encoding and the
/// order in which a write batch gives storage roots to accounts and accounts to the state trie.
/// </remarks>
public sealed unsafe class AirbenderTrieScopeProvider(
    IWorldStateScopeProvider.ICodeDb codeDb,
    IStateHeaderProvider stateHeaderProvider,
    ILogManager logManager) : IWorldStateScopeProvider
{
    private static readonly AccountDecoder _decoder = new();

    public bool HasRoot(BlockHeader? baseBlock) => HasRoot(baseBlock?.StateRoot ?? Keccak.EmptyTreeHash);

    private static bool HasRoot(Hash256 root)
    {
        ValueHash256 hash = root.ValueHash256;
        return Accelerators.AirbenderMptHasRoot((byte*)&hash);
    }

    public bool HasStateForTargetBlock(BlockHeader targetBlock) => this.HasRootForTarget(stateHeaderProvider, targetBlock);

    public bool TryBeginScopeAtTarget(BlockHeader targetBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope) =>
        this.TryBeginScopeAtBase(stateHeaderProvider, targetBlock, metrics, out scope);

    public bool TryBeginScope(BlockHeader? baseBlock, LocalMetrics metrics, [NotNullWhen(true)] out IWorldStateScopeProvider.IScope? scope)
    {
        Hash256 root = baseBlock?.StateRoot ?? Keccak.EmptyTreeHash;
        if (!HasRoot(root))
        {
            scope = null;
            return false;
        }

        scope = new Scope(root, codeDb, logManager);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Check(int status)
    {
        if (status < 0) ThrowTrie(status);
    }

    [DoesNotReturn]
    private static void ThrowTrie(int status) =>
        throw new TrieException(status == -1 ? "A trie node is missing from the witness" : "A witness trie node is malformed or its path does not end at the key length");

    private static void* Open(Hash256 root)
    {
        ValueHash256 hash = root.ValueHash256;
        return Accelerators.AirbenderMptOpen((byte*)&hash);
    }

    private static Hash256 RootOf(void* trie)
    {
        ValueHash256 hash = default;
        Check(Accelerators.AirbenderMptRoot(trie, (byte*)&hash));
        return new Hash256(in hash);
    }

    private sealed class Scope(Hash256 root, IWorldStateScopeProvider.ICodeDb codeDb, ILogManager logManager) : IWorldStateScopeProvider.IScope
    {
        private readonly void* _state = Open(root);
        private readonly Dictionary<AddressAsKey, Storage> _storages = [];

        public Hash256 RootHash { get; private set; } = root;

        public void UpdateRootHash() => RootHash = RootOf(_state);

        /// <remarks>libairbender splits the account's encoding; an encoding it does not know is decoded here.</remarks>
        [SkipLocalsInit]
        public Account? Get(Address address)
        {
            uint* fields = stackalloc uint[AccountFieldWords];
            byte* value;
            uint length;
            int status = Accelerators.AirbenderMptGetAccountFields(_state,
                (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(address.Bytes)), fields, &value, &length);
            if (status == 1) return null;
            if (status == 2)
            {
                RlpReader context = new(new ReadOnlySpan<byte>(value, (int)length));
                return _decoder.Decode(ref context, RlpBehaviors.TrustedInput);
            }

            Check(status);
            ulong nonce = fields[0] | (ulong)fields[1] << 32;
            ref UInt256 balance = ref *(UInt256*)(fields + 2);
            uint empty = fields[AccountFieldWords - 1];
            if (empty == 3) return new Account(nonce, in balance);
            Hash256 storageRoot = (empty & 1) != 0 ? Keccak.EmptyTreeHash : new Hash256(new ReadOnlySpan<byte>(fields + 10, Hash256.Size));
            Hash256 codeHash = (empty & 2) != 0 ? Keccak.OfAnEmptyString : new Hash256(new ReadOnlySpan<byte>(fields + 18, Hash256.Size));
            return new Account(nonce, in balance, storageRoot, codeHash);
        }

        private const int AccountFieldWords = 27;

        internal void Set(Address address, Account? account)
        {
            byte* key = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(address.Bytes));
            if (account is null)
            {
                Check(Accelerators.AirbenderMptSetAccount(_state, key, null, 0));
                return;
            }

            ulong nonce = account.Nonce;
            Check(Accelerators.AirbenderMptSetAccountFields(_state, key, (uint*)&nonce,
                (uint*)Unsafe.AsPointer(ref Unsafe.AsRef(in account.Balance)),
                (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(account.StorageRoot.Bytes)),
                (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(account.CodeHash.Bytes))));
        }

        public void HintGet(Address address, Account? account) { }

        public IWorldStateScopeProvider.ICodeDb CodeDb => codeDb;

        internal Storage LookupStorage(Address address)
        {
            ref Storage? storage = ref CollectionsMarshal.GetValueRefOrAddDefault(_storages, address, out bool exists);
            if (!exists)
            {
                int status;
                void* trie = Accelerators.AirbenderMptOpenStorage(_state,
                    (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(address.Bytes)), &status);
                if (trie is null)
                {
                    Check(status);
                    trie = Open(Get(address)?.StorageRoot ?? Keccak.EmptyTreeHash);
                }

                storage = new Storage(trie);
            }

            return storage!;
        }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) => LookupStorage(address);

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) =>
            new WriteBatch(this, estimatedAccountNum);

        public void Commit(ulong blockNumber) => UpdateRootHash();

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => Task.CompletedTask;

        public void ApplyBal(ReadOnlyBlockAccessList bal, CancellationToken cancellationToken = default) =>
            ScopeBalApplier.Apply(this, bal, logManager, cancellationToken);

        public void Dispose() { }
    }

    private sealed class Storage(void* trie) : IWorldStateScopeProvider.IStorageTree
    {
        internal void* Trie => trie;

        public Hash256 RootHash => RootOf(trie);

        [SkipLocalsInit]
        public void Get(in UInt256 index, out UInt256 value)
        {
            Unsafe.SkipInit(out value);
            Check(Accelerators.AirbenderMptGetSlot(trie, (uint*)Unsafe.AsPointer(ref Unsafe.AsRef(in index)), (uint*)Unsafe.AsPointer(ref value)));
        }

        public void HintSet(in UInt256 index) { }
    }

    private sealed class WriteBatch(Scope scope, int estimatedAccountCount) : IWorldStateScopeProvider.IWorldStateWriteBatch
    {
        private readonly Dictionary<AddressAsKey, Account?> _dirtyAccounts = [with(estimatedAccountCount)];
        private readonly Queue<(AddressAsKey, Hash256)> _dirtyStorages = [];

        public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

        public void Set(Address key, Account? account) => _dirtyAccounts[key] = account;

        public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address address, int estimatedEntries) =>
            new StorageBatch(scope.LookupStorage(address), this, address);

        internal void MarkDirty(Address address, Hash256 storageRoot) => _dirtyStorages.Enqueue((address, storageRoot));

        public void Dispose()
        {
            while (_dirtyStorages.TryDequeue(out (AddressAsKey, Hash256) entry))
            {
                (AddressAsKey key, Hash256 storageRoot) = entry;
                ref Account? slot = ref CollectionsMarshal.GetValueRefOrNullRef(_dirtyAccounts, key);
                bool isDirty = !Unsafe.IsNullRef(ref slot);
                Account? account = isDirty ? slot : scope.Get(key);
                // As TrieStoreScopeProvider: an account EIP-161 deletes in the block of its storage changes is gone.
                if (account is null) continue;
                account = account.WithChangedStorageRoot(storageRoot);
                if (isDirty) slot = account;
                else _dirtyAccounts[key] = account;
                Address address = key.Value;
                OnAccountUpdated?.Invoke(address, new IWorldStateScopeProvider.AccountUpdated(address, account));
            }

            OnAccountUpdated = null;
            // Inserts and updates before deletes, as StateTree.WriteAccounts in the guest.
            foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
            {
                if (kv.Value is not null) scope.Set(kv.Key.Value, kv.Value);
            }

            foreach (KeyValuePair<AddressAsKey, Account?> kv in _dirtyAccounts)
            {
                if (kv.Value is null) scope.Set(kv.Key.Value, null);
            }
        }
    }

    private sealed class StorageBatch(Storage storage, WriteBatch batch, Address address) : IWorldStateScopeProvider.IStorageWriteBatch
    {
        private bool _wasSetCalled;
        private bool _hasSelfDestruct;

        public void Set(in UInt256 index, in UInt256 value)
        {
            _wasSetCalled = true;
            Check(Accelerators.AirbenderMptSetSlot(storage.Trie, (uint*)Unsafe.AsPointer(ref Unsafe.AsRef(in index)), (uint*)Unsafe.AsPointer(ref Unsafe.AsRef(in value))));
        }

        public void Clear()
        {
            if (_wasSetCalled) throw new InvalidOperationException("Must call clear first in a storage write batch");
            Accelerators.AirbenderMptClear(storage.Trie);
            _hasSelfDestruct = true;
        }

        public void Dispose()
        {
            if (_wasSetCalled || _hasSelfDestruct) batch.MarkDirty(address, storage.RootHash);
        }
    }
}
