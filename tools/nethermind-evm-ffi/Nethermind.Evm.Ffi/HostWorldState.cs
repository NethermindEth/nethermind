// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.Ffi;

/// <summary>What one transaction changed, in the shape the host gets it back.</summary>
internal sealed class Diff
{
    public readonly List<(Address Address, Account? Account)> Accounts = [];
    public readonly List<(Address Address, UInt256 Key, UInt256 Value)> Storage = [];
    public readonly List<(ValueHash256 Hash, byte[] Code)> Code = [];

    public void Clear()
    {
        Accounts.Clear();
        Storage.Clear();
        Code.Clear();
    }
}

/// <summary>
/// A world-state backend that reads through to the host and records what execution writes.
/// </summary>
/// <remarks>
/// It builds no trie and returns no meaningful state root: the caller owns the state and derives
/// roots itself, so hashing here would be work nobody consumes. Reads are cached for the life of
/// the scope, which is what keeps the callback count at roughly one per account the transaction
/// has not already touched.
/// </remarks>
internal sealed unsafe class HostScopeProvider(NmEvmHost host, Diff diff) : IWorldStateScopeProvider
{
    /// A contract cannot exceed EIP-170's 24 KiB, so one buffer of that size answers every
    /// code read without a second call.
    private const int MaxCodeSize = 24 * 1024;

    public bool HasRoot(BlockHeader? baseBlock) => true;

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
        => new Scope(host, diff);

    private sealed class Scope(NmEvmHost host, Diff diff) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<Address, Account?> _accounts = new();
        private readonly Dictionary<Address, StorageTree> _storage = new();
        private readonly CodeDb _codeDb = new(host, diff);

        public Hash256 RootHash => Keccak.EmptyTreeHash;
        public void UpdateRootHash() { }
        public void Dispose() { }

        public Account? Get(Address address)
        {
            if (_accounts.TryGetValue(address, out Account? cached)) return cached;

            ulong nonce = 0;
            Span<byte> balance = stackalloc byte[32];
            Span<byte> codeHash = stackalloc byte[32];
            int found;
            fixed (byte* a = address.Bytes)
            fixed (byte* b = balance)
            fixed (byte* c = codeHash)
                found = host.GetAccount(host.Ctx, a, &nonce, b, c);

            Account? account = found == 0
                ? null
                : new Account(nonce, new UInt256(balance), Keccak.EmptyTreeHash, new Hash256(codeHash));
            _accounts[address] = account;
            return account;
        }

        public void HintGet(Address address, Account? account) => _accounts[address] = account;

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            if (!_storage.TryGetValue(address, out StorageTree? tree))
                _storage[address] = tree = new StorageTree(address, host);
            return tree;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
            => new WriteBatch(this, diff);

        public void Commit(ulong blockNumber) { }

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
            => Task.CompletedTask;

        internal void Apply(Address key, Account? account) => _accounts[key] = account;

        internal StorageTree Tree(Address key) => (StorageTree)CreateStorageTree(key);

        private sealed class WriteBatch(Scope scope, Diff diff) : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;

            public void Set(Address key, Account? account)
            {
                scope.Apply(key, account);
                diff.Accounts.Add((key, account));
                OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
            }

            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
                => new StorageWriteBatch(key, scope.Tree(key), diff);

            public void Dispose() { }
        }

        private sealed class StorageWriteBatch(Address address, StorageTree tree, Diff diff)
            : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Set(in UInt256 index, in UInt256 value)
            {
                tree.Written[index] = value;
                diff.Storage.Add((address, index, value));
            }

            /// <remarks>
            /// Self-destruct. The host learns the account is gone from the account change that
            /// follows, and clears its storage itself; enumerating the slots here would mean
            /// reading the whole contract back across the boundary to say nothing new.
            /// </remarks>
            public void Clear() => tree.Written.Clear();

            public void Dispose() { }
        }
    }

    private sealed class StorageTree(Address address, NmEvmHost host) : IWorldStateScopeProvider.IStorageTree
    {
        internal readonly Dictionary<UInt256, UInt256> Written = new();

        public Hash256 RootHash => Keccak.EmptyTreeHash;
        public void HintSet(in UInt256 index) { }

        public void Get(in UInt256 index, out UInt256 value)
        {
            if (Written.TryGetValue(index, out value)) return;

            Span<byte> key = stackalloc byte[32];
            Span<byte> result = stackalloc byte[32];
            index.ToLittleEndian(key);
            int found;
            fixed (byte* a = address.Bytes)
            fixed (byte* k = key)
            fixed (byte* v = result)
                found = host.GetStorage(host.Ctx, a, k, v);

            value = found == 0 ? UInt256.Zero : new UInt256(result);
        }
    }

    private sealed class CodeDb(NmEvmHost host, Diff diff) : IWorldStateScopeProvider.ICodeDb
    {
        private readonly Dictionary<ValueHash256, byte[]> _written = new();

        public byte[]? GetCode(in ValueHash256 codeHash)
        {
            if (_written.TryGetValue(codeHash, out byte[]? code)) return code;

            byte[] buffer = new byte[MaxCodeSize];
            Span<byte> hash = stackalloc byte[32];
            codeHash.Bytes.CopyTo(hash);
            int length;
            fixed (byte* h = hash)
            fixed (byte* b = buffer)
                length = host.GetCode(host.Ctx, h, b, buffer.Length);

            if (length < 0) return null;
            if (length > buffer.Length)
            {
                // The host reported a size past EIP-170. Honour it rather than truncate.
                buffer = new byte[length];
                fixed (byte* h = hash)
                fixed (byte* b = buffer)
                    length = host.GetCode(host.Ctx, h, b, buffer.Length);
                if (length < 0 || length > buffer.Length) return null;
            }
            return buffer[..length];
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new Setter(_written, diff);

        private sealed class Setter(Dictionary<ValueHash256, byte[]> written, Diff diff)
            : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code)
            {
                byte[] copy = code.ToArray();
                written[codeHash] = copy;
                diff.Code.Add((codeHash, copy));
            }

            public void Dispose() { }
        }
    }
}

/// <summary>Forwards BLOCKHASH to the host.</summary>
internal sealed unsafe class HostBlockhashProvider(NmEvmHost host) : IBlockhashProvider
{
    public Hash256? GetBlockhash(BlockHeader currentBlock, ulong number, IReleaseSpec spec)
    {
        Span<byte> hash = stackalloc byte[32];
        int found;
        fixed (byte* h = hash)
            found = host.GetBlockHash(host.Ctx, number, h);
        return found == 0 ? null : new Hash256(hash);
    }

    public Task Prefetch(BlockHeader currentBlock, CancellationToken token) => Task.CompletedTask;
}
