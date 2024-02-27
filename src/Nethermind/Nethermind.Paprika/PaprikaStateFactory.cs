using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Paprika.Chain;
using Paprika.Merkle;
using Paprika.Store;
using IWorldState = Paprika.Chain.IWorldState;
using PaprikaKeccak = Paprika.Crypto.Keccak;
using PaprikaAccount = Paprika.Account;
using System.Runtime.CompilerServices;

namespace Nethermind.Paprika;

public class PaprikaStateFactory : IStateFactory
{
    private static readonly long _sepolia = 32.GiB();
    private static readonly long _mainnet = 256.GiB();

    private static readonly TimeSpan _flushFileEvery = TimeSpan.FromSeconds(10);

    public static readonly byte[] ZeroByte = new byte[] { 0 };

    private readonly PagedDb _db;
    private readonly Blockchain _blockchain;
    private readonly Queue<(PaprikaKeccak keccak, uint number)> _poorManFinalizationQueue = new();
    private uint _lastFinalized = 0;

    public PaprikaStateFactory(string directory, IPaprikaConfig config)
    {
        var stateOptions = new CacheBudget.Options(config.CacheStatePerBlock, config.CacheStateBeyond);
        var merkleOptions = new CacheBudget.Options(config.CacheMerklePerBlock, config.CacheMerkleBeyond);

        _db = PagedDb.MemoryMappedDb(_mainnet, 64, directory, true);
        ComputeMerkleBehavior merkle = new(1, 1, Memoization.None);
        _blockchain = new Blockchain(_db, merkle, _flushFileEvery, stateOptions, merkleOptions);
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

    class ReadOnlyState(IReadOnlyWorldState wrapped) : IReadOnlyState
    {
        public bool TryGet(Address address, out AccountStruct account)
        {
            PaprikaAccount retrieved = wrapped.GetAccount(Convert(address));
            bool hasEmptyStorageAndCode = retrieved.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          retrieved.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (retrieved.Balance.IsZero &&
                retrieved.Nonce.IsZero &&
                hasEmptyStorageAndCode)
            {
                account = default;
                return false;
            }

            if (hasEmptyStorageAndCode)
            {
                account = new AccountStruct(retrieved.Nonce, retrieved.Balance);
                return true;
            }

            account = new AccountStruct(retrieved.Nonce, retrieved.Balance, Convert(retrieved.StorageRootHash),
                Convert(retrieved.CodeHash));
            return true;
        }

        [SkipLocalsInit]
        public byte[] GetStorageAt(in StorageCell cell)
        {
            // bytes are used for two purposes, first for the key encoding and second, for the result handling
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            Span<byte> value = wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return value.IsEmpty ? ZeroByte : value.ToArray();
        }

        [SkipLocalsInit]
        public byte[] GetStorageAt(Address address, in ValueHash256 hash)
        {
            Span<byte> bytes = stackalloc byte[32];
            Span<byte> value = wrapped.GetStorage(Convert(address), new PaprikaKeccak(hash.Bytes), bytes);
            return value.IsEmpty ? ZeroByte : value.ToArray();
        }

        public Hash256 StateRoot => Convert(wrapped.Hash);

        public void Dispose() => wrapped.Dispose();
    }

    class State(IWorldState wrapped, PaprikaStateFactory factory) : IState
    {
        public void Set(Address address, in AccountStruct account, bool isNewHint = false)
        {
            PaprikaKeccak key = Convert(address);

            if (account.IsNull)
            {
                wrapped.DestroyAccount(key);
            }
            else
            {
                PaprikaAccount actual = new(account.Balance, account.Nonce, Convert(account.CodeHash),
                    Convert(account.StorageRoot));
                wrapped.SetAccount(key, actual, isNewHint);
            }
        }

        public bool TryGet(Address address, out AccountStruct account)
        {
            PaprikaAccount retrieved = wrapped.GetAccount(Convert(address));
            bool hasEmptyStorageAndCode = retrieved.CodeHash == PaprikaKeccak.OfAnEmptyString &&
                                          retrieved.StorageRootHash == PaprikaKeccak.EmptyTreeHash;
            if (retrieved.Balance.IsZero &&
                retrieved.Nonce.IsZero &&
                hasEmptyStorageAndCode)
            {
                account = default;
                return false;
            }

            if (hasEmptyStorageAndCode)
            {
                account = new AccountStruct(retrieved.Nonce, retrieved.Balance);
                return true;
            }

            account = new AccountStruct(retrieved.Nonce, retrieved.Balance, Convert(retrieved.StorageRootHash),
                Convert(retrieved.CodeHash));
            return true;
        }

        [SkipLocalsInit]
        public byte[] GetStorageAt(in StorageCell cell)
        {
            // bytes are used for two purposes, first for the key encoding and second, for the result handling
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            Span<byte> value = wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return value.IsEmpty ? ZeroByte : value.ToArray();
        }

        [SkipLocalsInit]
        public byte[] GetStorageAt(Address address, in ValueHash256 hash)
        {
            Span<byte> bytes = stackalloc byte[32];
            Span<byte> value = wrapped.GetStorage(Convert(address), new PaprikaKeccak(hash.Bytes), bytes);
            return value.IsEmpty ? ZeroByte : value.ToArray();
        }

        public void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(cell.Index, key);
            PaprikaKeccak converted = Convert(cell.Address);
            wrapped.SetStorage(converted, new PaprikaKeccak(key),
                value.IsZero() ? default : value);
        }

        public void Commit(long blockNumber)
        {
            wrapped.Commit((uint)blockNumber);
            factory.Committed(wrapped);
        }

        public void Reset() => wrapped.Reset();

        public Hash256 StateRoot => Convert(wrapped.Hash);

        public void Dispose() => wrapped.Dispose();
    }

    private void Committed(IWorldState block)
    {
        const int poorManFinality = 64;

        lock (_poorManFinalizationQueue)
        {
            // Find all the ancestors that are after last finalized.
            (uint blockNumber, PaprikaKeccak hash)[] beyondFinalized =
                block.Stats.Ancestors.Where(ancestor => ancestor.blockNumber > _lastFinalized).ToArray();

            if (beyondFinalized.Length < poorManFinality)
            {
                // There number of ancestors is not as big as needed.
                return;
            }

            // If there's more than poorManFinality, finalize the oldest and memoize its number
            (uint blockNumber, PaprikaKeccak hash) oldest = beyondFinalized.Min(blockNo => blockNo);

            _lastFinalized = oldest.blockNumber;
            _blockchain.Finalize(oldest.hash);
        }
    }
}
