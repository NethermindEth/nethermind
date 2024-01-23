using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Paprika.Chain;
using Paprika.Data;
using Paprika.Merkle;
using Paprika.Store;
using IWorldState = Paprika.Chain.IWorldState;
using PaprikaKeccak = Paprika.Crypto.Keccak;
using PaprikaAccount = Paprika.Account;

namespace Nethermind.Paprika;

public class PaprikaStateFactory : IStateFactory
{
    private static readonly ulong _sepolia = (ulong)32.GiB();
    private static readonly TimeSpan _flushFileEvery = TimeSpan.FromSeconds(10);

    private readonly PagedDb _db;
    private readonly Blockchain _blockchain;
    private readonly Queue<(PaprikaKeccak keccak, uint number)> _poorManFinalizationQueue = new();
    private uint _lastFinalized = 0;

    public PaprikaStateFactory(string directory)
    {
        _db = PagedDb.MemoryMappedDb(_sepolia, 64, directory, true);
        ComputeMerkleBehavior merkle = new(1, 1);
        _blockchain = new Blockchain(_db, merkle, _flushFileEvery, new CacheBudget.Options(1000));
        _blockchain.Flushed += (_, flushed) =>
            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(flushed.blockNumber));
    }

    public PaprikaStateFactory()
    {
        _db = PagedDb.NativeMemoryDb((ulong)64.MB());
        ComputeMerkleBehavior merkle = new(1, 1);
        _blockchain = new Blockchain(_db, merkle, _flushFileEvery, new CacheBudget.Options(1000));
        _blockchain.Flushed += (_, flushed) =>
            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(flushed.blockNumber));
    }

    public IState Get(Hash256 stateRoot) => new State(_blockchain.StartNew(Convert(stateRoot)), this);

    public IReadOnlyState GetReadOnly(Hash256 stateRoot) =>
        new ReadOnlyState(_blockchain.StartReadOnly(Convert(stateRoot)));

    public bool HasRoot(Hash256 stateRoot) => _blockchain.HasState(Convert(stateRoot));

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public async ValueTask DisposeAsync()
    {
        await _blockchain.DisposeAsync();
        _db.Dispose();
    }

    private static PaprikaKeccak Convert(Hash256 keccak) => new(keccak.Bytes);
    private static PaprikaKeccak Convert(in ValueHash256 keccak) => new(keccak.Bytes);
    private static Hash256 Convert(PaprikaKeccak keccak) => new(keccak.BytesAsSpan);
    private static PaprikaKeccak Convert(Address address) => Convert(ValueKeccak.Compute(address.Bytes));

    // shamelessly stolen from storage trees
    private const int CacheSize = 1024;
    private static readonly byte[][] _cache = new byte[CacheSize][];

    private static void GetKey(in UInt256 index, in Span<byte> key)
    {
        if (index < CacheSize)
        {
            _cache[(int)index].CopyTo(key);
            return;
        }

        index.ToBigEndian(key);

        // in situ calculation
        KeccakHash.ComputeHashBytesToSpan(key, key);
    }

    static PaprikaStateFactory()
    {
        Span<byte> buffer = stackalloc byte[32];
        for (int i = 0; i < CacheSize; i++)
        {
            UInt256 index = (UInt256)i;
            index.ToBigEndian(buffer);
            _cache[i] = Keccak.Compute(buffer).BytesToArray();
        }
    }

    public void Finalize(Hash256 finalizedStateRoot, long finalizedNumber)
    {
        // TODO: more
        // _blockchain.Finalize(Convert(finalizedStateRoot));
    }

    class ReadOnlyState : IReadOnlyState
    {
        private readonly IReadOnlyWorldState _wrapped;

        public ReadOnlyState(IReadOnlyWorldState wrapped)
        {
            _wrapped = wrapped;
        }

        public Account? Get(Address address)
        {
            PaprikaAccount account = _wrapped.GetAccount(Convert(address));
            bool hasEmptyStorageAndCode = account.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          account.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (account.Balance.IsZero &&
                account.Nonce.IsZero &&
                hasEmptyStorageAndCode)
                return null;

            if (hasEmptyStorageAndCode)
                return new Account(account.Nonce, account.Balance);

            return new Account(account.Nonce, account.Balance, Convert(account.StorageRootHash),
                Convert(account.CodeHash));
        }

        public Account? Get(ValueHash256 hash)
        {
            PaprikaAccount account = _wrapped.GetAccount(Convert(hash));
            bool hasEmptyStorageAndCode = account.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          account.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (account.Balance.IsZero &&
                account.Nonce.IsZero &&
                hasEmptyStorageAndCode)
                return null;

            if (hasEmptyStorageAndCode)
                return new Account(account.Nonce, account.Balance);

            return new Account(account.Nonce, account.Balance, Convert(account.StorageRootHash),
                Convert(account.CodeHash));
        }

        public byte[] GetStorageAt(in StorageCell cell)
        {
            // bytes are used for two purposes, first for the key encoding and second, for the result handling
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            Span<byte> value = _wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return value.IsEmpty ? new byte[] { 0 } : value.ToArray();
        }

        public byte[] GetStorageAt(Address address, in ValueHash256 hash)
        {
            Span<byte> bytes = stackalloc byte[32];
            Span<byte> value = _wrapped.GetStorage(Convert(address), new PaprikaKeccak(hash.Bytes), bytes);
            return value.IsEmpty ? new byte[] { 0 } : value.ToArray();
        }

        public Hash256 StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }

    class State : IState
    {
        private readonly IWorldState _wrapped;
        private readonly PaprikaStateFactory _factory;

        public State(IWorldState wrapped, PaprikaStateFactory factory)
        {
            _wrapped = wrapped;
            _factory = factory;
        }

        public void Set(Address address, Account? account)
        {
            PaprikaKeccak key = Convert(address);

            if (account == null)
            {
                _wrapped.DestroyAccount(key);
            }
            else
            {
                PaprikaAccount actual = new(account.Balance, account.Nonce, Convert(account.CodeHash),
                    Convert(account.StorageRoot));
                _wrapped.SetAccount(key, actual);
            }
        }

        public void Set(ValueHash256 hash, Account? account)
        {
            PaprikaKeccak key = Convert(hash);

            if (account == null)
            {
                _wrapped.DestroyAccount(key);
            }
            else
            {
                PaprikaAccount actual = new(account.Balance, account.Nonce, Convert(account.CodeHash),
                    Convert(account.StorageRoot));
                _wrapped.SetAccount(key, actual);
            }
        }

        public void Set(ReadOnlySpan<byte> keyPath, int targetKeyLength, Hash256 keccak)
        {
            NibblePath path = NibblePath.FromKey(keyPath).SliceTo(targetKeyLength);
            _wrapped.SetAccountHash(path, Convert(keccak));
        }

        public Account? Get(Address address)
        {
            PaprikaAccount account = _wrapped.GetAccount(Convert(address));
            bool hasEmptyStorageAndCode = account.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          account.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (account.Balance.IsZero &&
                account.Nonce.IsZero &&
                hasEmptyStorageAndCode)
                return null;

            if (hasEmptyStorageAndCode)
                return new Account(account.Nonce, account.Balance);

            return new Account(account.Nonce, account.Balance, Convert(account.StorageRootHash),
                Convert(account.CodeHash));
        }

        public Account? Get(ValueHash256 hash)
        {
            PaprikaAccount account = _wrapped.GetAccount(Convert(hash));
            bool hasEmptyStorageAndCode = account.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          account.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (account.Balance.IsZero &&
                account.Nonce.IsZero &&
                hasEmptyStorageAndCode)
                return null;

            if (hasEmptyStorageAndCode)
                return new Account(account.Nonce, account.Balance);

            return new Account(account.Nonce, account.Balance, Convert(account.StorageRootHash),
                Convert(account.CodeHash));
        }

        public byte[] GetStorageAt(in StorageCell cell)
        {
            // bytes are used for two purposes, first for the key encoding and second, for the result handling
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            Span<byte> value = _wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return value.IsEmpty ? new byte[] { 0 } : value.ToArray();
        }

        public byte[] GetStorageAt(Address address, in ValueHash256 hash)
        {
            Span<byte> bytes = stackalloc byte[32];
            Span<byte> value = _wrapped.GetStorage(Convert(address), new PaprikaKeccak(hash.Bytes), bytes);
            return value.IsEmpty ? new byte[] { 0 } : value.ToArray();
        }

        public void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(cell.Index, key);
            PaprikaKeccak converted = Convert(cell.Address);
            _wrapped.SetStorage(converted, new PaprikaKeccak(key),
                value.IsZero() ? ReadOnlySpan<byte>.Empty : value);
        }

        public void Commit(long blockNumber)
        {
            _wrapped.Commit((uint)blockNumber);
            _factory.Committed(_wrapped, (uint)blockNumber);
        }

        public void Reset() => _wrapped.Reset();

        public Hash256 StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }

    private void Committed(IWorldState block, uint committedAt)
    {
        const int poorManFinality = 96;

        lock (_poorManFinalizationQueue)
        {
            _poorManFinalizationQueue.Enqueue((block.Hash, committedAt));

            while (_poorManFinalizationQueue.TryPeek(out (PaprikaKeccak hash, uint number) peeked))
            {
                if ((committedAt - peeked.number <= poorManFinality))
                {
                    break;
                }

                _poorManFinalizationQueue.Dequeue();

                if (peeked.number > _lastFinalized)
                {
                    _blockchain.Finalize(peeked.hash);
                    _lastFinalized = peeked.number;
                }
            }
        }
    }
}
