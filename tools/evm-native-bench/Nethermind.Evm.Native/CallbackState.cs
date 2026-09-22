using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm.Native;

/// The four reads the host answers, as a C ABI. This is the boundary rbuilder would own:
/// Rust holds the authoritative state, the EVM borrows through these.
public unsafe struct StateCallbacks
{
    /// address(20) -> nonce(8), balance(32 LE), codeHash(32). Returns 1 when the account exists.
    public delegate* unmanaged<byte*, byte*, byte*, byte*, int> GetAccount;
    /// address(20), key(32 LE) -> value(32 LE). Returns 1 when a value was written.
    public delegate* unmanaged<byte*, byte*, byte*, int> GetStorage;
    /// codeHash(32), buffer, buffer length -> code length, or -1 when unknown.
    public delegate* unmanaged<byte*, byte*, int, int> GetCode;
}

/// A world-state backend that reads through to the host and keeps writes in memory.
/// It computes no trie and no state root: rbuilder derives those itself from the diff, so the
/// EVM never needs to hash anything here.
internal sealed unsafe class CallbackScopeProvider(StateCallbacks callbacks) : IWorldStateScopeProvider
{
    public long AccountReads;
    public long StorageReads;
    public long CodeReads;

    public bool HasRoot(BlockHeader? baseBlock) => true;

    public IWorldStateScopeProvider.IScope BeginScope(BlockHeader? baseBlock, LocalMetrics metrics)
        => new Scope(this, callbacks);

    private sealed class Scope(CallbackScopeProvider owner, StateCallbacks cb) : IWorldStateScopeProvider.IScope
    {
        private readonly Dictionary<Address, Account?> _accounts = new();
        private readonly Dictionary<Address, StorageTree> _storage = new();
        private readonly CodeDb _codeDb = new(cb, owner);

        public Hash256 RootHash => Keccak.EmptyTreeHash;
        public void UpdateRootHash() { }
        public void Dispose() { }

        public Account? Get(Address address)
        {
            if (_accounts.TryGetValue(address, out Account? cached)) return cached;

            Span<byte> nonce = stackalloc byte[8];
            Span<byte> balance = stackalloc byte[32];
            Span<byte> codeHash = stackalloc byte[32];
            int found;
            fixed (byte* a = address.Bytes)
            fixed (byte* n = nonce)
            fixed (byte* b = balance)
            fixed (byte* c = codeHash)
                found = cb.GetAccount(a, n, b, c);
            owner.AccountReads++;

            Account? account = found == 0
                ? null
                : new Account(
                    BitConverter.ToUInt64(nonce),
                    new UInt256(balance),
                    Keccak.EmptyTreeHash,
                    new Hash256(codeHash));
            _accounts[address] = account;
            return account;
        }

        public void HintGet(Address address, Account? account) => _accounts[address] = account;

        public IWorldStateScopeProvider.ICodeDb CodeDb => _codeDb;

        public IWorldStateScopeProvider.IStorageTree CreateStorageTree(Address address)
        {
            if (!_storage.TryGetValue(address, out StorageTree? tree))
                _storage[address] = tree = new StorageTree(address, cb, owner);
            return tree;
        }

        public IWorldStateScopeProvider.IWorldStateWriteBatch StartWriteBatch(int estimatedAccountNum)
            => new WriteBatch(this);

        public void Commit(ulong blockNumber) { }

        public Task HintBal(ReadOnlyBlockAccessList bal, IWorldStateScopeProvider.IAsyncBalReaderSink? sink = null)
            => Task.CompletedTask;

        internal void Apply(Address key, Account? account) => _accounts[key] = account;

        private sealed class WriteBatch(Scope scope) : IWorldStateScopeProvider.IWorldStateWriteBatch
        {
            public event EventHandler<IWorldStateScopeProvider.AccountUpdated>? OnAccountUpdated;
            public void Set(Address key, Account? account)
            {
                scope.Apply(key, account);
                OnAccountUpdated?.Invoke(this, new IWorldStateScopeProvider.AccountUpdated(key, account));
            }
            public IWorldStateScopeProvider.IStorageWriteBatch CreateStorageWriteBatch(Address key, int estimatedEntries)
                => new StorageWriteBatch((StorageTree)scope.CreateStorageTree(key));
            public void Dispose() { }
        }

        private sealed class StorageWriteBatch(StorageTree tree) : IWorldStateScopeProvider.IStorageWriteBatch
        {
            public void Set(in UInt256 index, in UInt256 value) => tree.Written[index] = value;
            public void Clear() => tree.Written.Clear();
            public void Dispose() { }
        }
    }

    private sealed class StorageTree(Address address, StateCallbacks cb, CallbackScopeProvider owner)
        : IWorldStateScopeProvider.IStorageTree
    {
        internal readonly Dictionary<UInt256, UInt256> Written = new();
        public Hash256 RootHash => Keccak.EmptyTreeHash;
        public void HintSet(in UInt256 index) { }

        public void Get(in UInt256 index, out UInt256 value)
        {
            if (Written.TryGetValue(index, out value)) return;

            Span<byte> key = stackalloc byte[32];
            Span<byte> outValue = stackalloc byte[32];
            index.ToLittleEndian(key);
            int found;
            fixed (byte* a = address.Bytes)
            fixed (byte* k = key)
            fixed (byte* v = outValue)
                found = cb.GetStorage(a, k, v);
            owner.StorageReads++;
            value = found == 0 ? UInt256.Zero : new UInt256(outValue);
        }
    }

    private sealed class CodeDb(StateCallbacks cb, CallbackScopeProvider owner) : IWorldStateScopeProvider.ICodeDb
    {
        private readonly Dictionary<ValueHash256, byte[]> _written = new();

        public byte[]? GetCode(in ValueHash256 codeHash)
        {
            if (_written.TryGetValue(codeHash, out byte[]? code)) return code;

            byte[] buffer = new byte[64 * 1024];
            Span<byte> hash = stackalloc byte[32];
            codeHash.Bytes.CopyTo(hash);
            int length;
            fixed (byte* h = hash)
            fixed (byte* b = buffer)
                length = cb.GetCode(h, b, buffer.Length);
            owner.CodeReads++;
            if (length < 0) return null;
            return buffer[..length];
        }

        public IWorldStateScopeProvider.ICodeSetter BeginCodeWrite() => new Setter(_written);

        private sealed class Setter(Dictionary<ValueHash256, byte[]> written) : IWorldStateScopeProvider.ICodeSetter
        {
            public void Set(in ValueHash256 codeHash, ReadOnlySpan<byte> code) => written[codeHash] = code.ToArray();
            public void Dispose() { }
        }
    }
}
