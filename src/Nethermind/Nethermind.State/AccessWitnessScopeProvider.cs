// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.State;

/// <summary>
/// Decorates a scope provider to record, independently of the storage witness, every account/slot the EVM
/// touched (<see cref="TouchedKeys"/>) and every contract code it loaded (<see cref="Codes"/>) when the scope
/// is opened with <c>trackWitness</c>. These complete an execution witness alongside the trie-node RLPs that
/// <see cref="TrieWitnessScopeProvider"/> produces; the consumer assembles the full witness.
/// </summary>
/// <remarks>
/// Recording rides on the scope's own read path: the EVM reads every account/slot before writing it, so the
/// wrapped <c>Get</c> / storage-tree <c>Get</c> / code-db <c>GetCode</c> observe everything the witness needs.
/// <see cref="IWorldStateScopeProvider.IScope.Witness"/> is forwarded to the inner scope unchanged. The
/// accessed data is exposed for the lifetime of the current scope; safe as a single "current scope" reference
/// because each pooled witness env runs one scope at a time.
/// </remarks>
public class AccessWitnessScopeProvider(IWorldStateScopeProvider baseProvider) : IWorldStateScopeProvider
{
    private static readonly Dictionary<AddressAsKey, HashSet<UInt256>> s_noKeys = [];

    private AccessWitnessScope? _current;

    public bool HasRoot(BlockHeader? baseBlock) => baseProvider.HasRoot(baseBlock);

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, bool trackWitness = false) =>
        trackWitness
            // Propagate trackWitness to the inner wrapper so it produces the storage (trie-node) witness.
            ? _current = new AccessWitnessScope(baseProvider.BeginScope(baseBlock, true))
            : baseProvider.BeginScope(baseBlock, false);

    /// <summary>Accounts touched by the current witness scope and, per account, the touched storage slots.</summary>
    public IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> TouchedKeys => _current?.TouchedKeys ?? s_noKeys;

    /// <summary>Contract bytecode read by the current witness scope, deduplicated by code hash.</summary>
    public IReadOnlyCollection<byte[]> Codes => _current?.Codes ?? [];

    private sealed class AccessWitnessScope(IWorldStateScopeProvider.IScope baseScope) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<AddressAsKey, HashSet<UInt256>> _touchedKeys = [];
        private readonly AccessWitnessCodeDb _codeDb = new(baseScope.CodeDb);

        public IReadOnlyDictionary<AddressAsKey, HashSet<UInt256>> TouchedKeys => _touchedKeys;
        public IReadOnlyCollection<byte[]> Codes => _codeDb.Codes;

        private void Touch(Address address)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_touchedKeys, address, out _);
            slots ??= [];
        }

        private void Touch(Address address, in UInt256 index)
        {
            ref HashSet<UInt256>? slots = ref CollectionsMarshal.GetValueRefOrAddDefault(_touchedKeys, address, out _);
            slots ??= [];
            slots.Add(index);
        }

        public Account? Get(Address address)
        {
            Touch(address);
            return baseScope.Get(address);
        }

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address) =>
            new AccessWitnessStorageTree(baseScope.CreateStorageTree(address), address, this);

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        // The storage witness (trie nodes) is produced by the inner scope; pass it straight through.
        public IReadOnlyList<byte[]>? Witness => baseScope.Witness;

        // The rest is plain delegation to the wrapped scope.
        public Hash256 RootHash => baseScope.RootHash;
        public void UpdateRootHash() => baseScope.UpdateRootHash();
        public void HintGet(Address address, Account? account) => baseScope.HintGet(address, account);
        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum) => baseScope.StartWriteBatch(estimatedAccountNum);
        public void Commit(long blockNumber) => baseScope.Commit(blockNumber);
        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null) => baseScope.HintBal(bal, sink);
        public void Dispose() => baseScope.Dispose();

        private sealed class AccessWitnessStorageTree(IWorldStateScopeProvider.IStorageTree baseTree, Address address, AccessWitnessScope scope) : IWorldStateScopeProvider.IStorageTree
        {
            public Hash256 RootHash => baseTree.RootHash;

            public byte[] Get(in UInt256 index)
            {
                scope.Touch(address, in index);
                return baseTree.Get(in index);
            }

            public void HintSet(in UInt256 index, byte[]? value) => baseTree.HintSet(in index, value);

            // JS-tracer-only lookup by hash; not part of the standard execution witness.
            public byte[] Get(in ValueHash256 hash) => baseTree.Get(in hash);
        }

        private sealed class AccessWitnessCodeDb(IWorldStateScopeProvider.ICodeDb baseCodeDb) : IWorldStateScopeProvider.ICodeDb
        {
            private readonly Dictionary<ValueHash256, byte[]> _bytecodes =
                new(GenericEqualityComparer.GetOptimized<ValueHash256>());

            public IReadOnlyCollection<byte[]> Codes => _bytecodes.Values;

            public byte[]? GetCode(in ValueHash256 codeHash)
            {
                byte[]? code = baseCodeDb.GetCode(in codeHash);
                // Empty code is never part of a witness; deployed-this-block code is served from the state
                // provider's batch and never reaches here, so it is correctly excluded.
                if (code is { Length: > 0 }) _bytecodes.TryAdd(codeHash, code);
                return code;
            }

            public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => baseCodeDb.BeginCodeWrite();
            public bool ContainsCode(in ValueHash256 codeHash) => baseCodeDb.ContainsCode(in codeHash);
            public void MarkCodePersisted(in ValueHash256 codeHash) => baseCodeDb.MarkCodePersisted(in codeHash);
        }
    }
}
