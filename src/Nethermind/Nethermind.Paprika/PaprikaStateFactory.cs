// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.Trie.Pruning;
using Paprika.Chain;
using Paprika.Merkle;
using Paprika.Store;
using IWorldState = Paprika.Chain.IWorldState;
using PaprikaKeccak = Paprika.Crypto.Keccak;
using PaprikaAccount = Paprika.Account;

namespace Nethermind.Paprika;

[SkipLocalsInit]
public class PaprikaStateFactory : IStateFactory
{
    private readonly ILogger _logger;

    private static readonly TimeSpan _flushFileEvery = TimeSpan.FromMinutes(10);

    private readonly PagedDb _db;
    private readonly Blockchain _blockchain;
    private readonly IReadOnlyWorldStateAccessor _accessor;
    private readonly Queue<(PaprikaKeccak keccak, long blockNumber)> _poorManFinalizationQueue = new();

    public PaprikaStateFactory()
    {
        _db = PagedDb.NativeMemoryDb(32 * 1024);
        var merkle = new ComputeMerkleBehavior(ComputeMerkleBehavior.ParallelismNone);
        _blockchain = new Blockchain(_db, merkle);
        _blockchain.Flushed += (_, flushed) =>
            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(flushed.blockNumber));

        _accessor = _blockchain.BuildReadOnlyAccessor();

        _logger = LimboLogs.Instance.GetClassLogger();
    }

    public PaprikaStateFactory(string directory, IPaprikaConfig config, int physicalCores, ILogManager logManager)
    {
        _logger = logManager.GetClassLogger();
        var stateOptions = new CacheBudget.Options(config.CacheStatePerBlock, config.CacheStateBeyond);
        var merkleOptions = new CacheBudget.Options(config.CacheMerklePerBlock, config.CacheMerkleBeyond);

        _db = PagedDb.MemoryMappedDb(config.SizeInGb.GiB(), (byte)config.HistoryDepth, directory, flushToDisk: true);

        var parallelism = config.ParallelMerkle ? physicalCores : ComputeMerkleBehavior.ParallelismNone;

        ComputeMerkleBehavior merkle = new(parallelism);
        _blockchain = new Blockchain(_db, merkle, _flushFileEvery, stateOptions, merkleOptions);
        _blockchain.Flushed += (_, flushed) =>
            ReorgBoundaryReached?.Invoke(this, new ReorgBoundaryReached(flushed.blockNumber));

        Prefetch = config.Prefetch;

        _blockchain.FlusherFailure += (_, exception) =>
        {
            _logger.Error("Paprika's Flusher task failed and stopped, throwing the following exception", exception);
        };

        _accessor = _blockchain.BuildReadOnlyAccessor();
    }

    /// <summary>
    /// Provides information whether prefetching was configured in <see cref="IPaprikaConfig"/>.
    /// </summary>
    private bool Prefetch { get; }

    public IState BuildFor(Hash256 stateRoot, bool prefetchMerkle)
    {
        return new State(_blockchain.StartNew(Convert(stateRoot)), this, prefetchMerkle);
    }

    public IReadOnlyState GetReadOnly(Hash256? stateRoot)
    {
        return new ReadOnlyState(stateRoot != null
            ? _blockchain.StartReadOnly(Convert(stateRoot))
            : _blockchain.StartReadOnlyLatestFinalized());
    }

    public bool HasRoot(Hash256 stateRoot)
    {
        return _accessor.HasState(Convert(stateRoot));
    }

    public bool TryGet(Hash256 stateRoot, Address address, out AccountStruct account)
    {
        return ConvertPaprikaAccount(_accessor.GetAccount(Convert(stateRoot), Convert(address)), out account);
    }

    public ReadOnlySpan<byte> GetStorage(Hash256 stateRoot, scoped in Address address, in UInt256 index)
    {
        Span<byte> bytes = stackalloc byte[32];
        GetKey(index, bytes);

        bytes = _accessor.GetStorage(Convert(stateRoot), Convert(address), new PaprikaKeccak(bytes), bytes);
        return MaterializeStorageValue(bytes);
    }

    public event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;

    public async ValueTask DisposeAsync()
    {
        await _blockchain.DisposeAsync();
        _db.Dispose();
    }

    private static PaprikaKeccak Convert(Hash256 keccak) => new(keccak.Bytes);
    private static PaprikaKeccak Convert(in ValueHash256 keccak) => new(keccak.Bytes);
    private static Hash256 Convert(PaprikaKeccak keccak) => new(keccak.BytesAsSpan);
    private static PaprikaKeccak Convert(Address address) => Convert(KeccakCache.Compute(address.Bytes));

    // shamelessly stolen from storage trees
    private const int CacheSize = 1024;
    private static readonly byte[][] Cache = new byte[CacheSize][];

    private static void GetKey(in UInt256 index, Span<byte> key)
    {
        if (index < CacheSize)
        {
            Cache[(int)index].CopyTo(key);
            return;
        }

        index.ToBigEndian(key);

        KeccakCache.Compute(key).BytesAsSpan.CopyTo(key);
    }

    //TODO: optimize by removing materialization and replacing it with value capturing construct like Vector256
    private static ReadOnlySpan<byte> MaterializeStorageValue(scoped ReadOnlySpan<byte> value)
    {
        return value.IsEmpty ? StorageTree.EmptyBytes : value.ToArray();
    }

    static PaprikaStateFactory()
    {
        Span<byte> buffer = stackalloc byte[32];
        for (int i = 0; i < CacheSize; i++)
        {
            UInt256 index = (UInt256)i;
            index.ToBigEndian(buffer);
            Cache[i] = Keccak.Compute(buffer).BytesToArray();
        }
    }

    public void Finalize(Hash256 finalizedStateRoot, long finalizedNumber)
    {
        // TODO: more
        // _blockchain.Finalize(Convert(finalizedStateRoot));
    }

    private static bool ConvertPaprikaAccount(in PaprikaAccount retrieved, out AccountStruct account)
    {
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
    private static ReadOnlySpan<byte> GetStorageAtImpl(IStateStorageAccessor worldState, scoped in StorageCell cell)
    {
        if (cell.IsHash)
        {
            return GetStorageAtImpl(worldState, cell.Address, cell.Hash);
        }

        Span<byte> bytes = stackalloc byte[32];
        GetKey(cell.Index, bytes);

        bytes = worldState.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
        return MaterializeStorageValue(bytes);
    }

    [SkipLocalsInit]
    private static ReadOnlySpan<byte> GetStorageAtImpl(IStateStorageAccessor worldState, Address address,
        scoped in ValueHash256 hash)
    {
        Span<byte> bytes = stackalloc byte[32];
        bytes = worldState.GetStorage(Convert(address), new PaprikaKeccak(hash.Bytes), bytes);
        return MaterializeStorageValue(bytes);
    }

    [SkipLocalsInit]
    class ReadOnlyState(IReadOnlyWorldState wrapped) : IReadOnlyState
    {
        public bool TryGet(Address address, out AccountStruct account)
        {
            return ConvertPaprikaAccount(wrapped.GetAccount(Convert(address)), out account);
        }

        public ReadOnlySpan<byte> GetStorageAt(scoped in StorageCell cell) => GetStorageAtImpl(wrapped, cell);

        public ReadOnlySpan<byte> GetStorageAt(Address address, in ValueHash256 hash) =>
            GetStorageAtImpl(wrapped, address, hash);

        public Hash256 StateRoot => Convert(wrapped.Hash);

        public void Dispose() => wrapped.Dispose();
    }

    class State : IState
    {
        private readonly IWorldState _wrapped;
        private readonly PaprikaStateFactory _factory;
        private readonly bool _prefetchMerkle;
        private readonly HashSet<int> _prefetched;
        private IPreCommitPrefetcher? _prefetch;

        public State(IWorldState wrapped, PaprikaStateFactory factory, bool prefetchMerkle)
        {
            _wrapped = wrapped;
            _factory = factory;
            _prefetchMerkle = prefetchMerkle && factory.Prefetch;
            _prefetched = new HashSet<int>();

            EnsurePrefetcher();
        }

        private void EnsurePrefetcher()
        {
            if (_prefetchMerkle)
            {
                _prefetched.Clear();
                _prefetch = _wrapped.OpenPrefetcher();
                return;
            }

            _prefetch = null;
        }

        public void Set(Address address, Account? account, bool isNewHint = false)
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
                _wrapped.SetAccount(key, actual, isNewHint);
            }
        }

        public bool TryGet(Address address, out AccountStruct account)
        {
            return ConvertPaprikaAccount(_wrapped.GetAccount(Convert(address)), out account);
        }

        public ReadOnlySpan<byte> GetStorageAt(scoped in StorageCell cell) => GetStorageAtImpl(_wrapped, cell);

        public ReadOnlySpan<byte> GetStorageAt(Address address, in ValueHash256 hash) =>
            GetStorageAtImpl(_wrapped, address, hash);

        [SkipLocalsInit]
        public void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(cell.Index, key);
            PaprikaKeccak converted = Convert(cell.Address);

            // mimics StorageTree.SetInternal
            if (value.IsZero())
                value = ReadOnlySpan<byte>.Empty;

            _wrapped.SetStorage(converted, new PaprikaKeccak(key), value);
        }

        [SkipLocalsInit]
        public void StorageMightBeSet(in StorageCell cell)
        {
            if (_prefetch == null || !_prefetched.Add(cell.GetHashCode()))
                return;

            PaprikaKeccak contract = Convert(cell.Address);

            if (cell.IsHash)
            {
                _prefetch.PrefetchStorage(contract, Convert(cell.Hash));
            }
            else
            {
                Span<byte> bytes = stackalloc byte[32];
                GetKey(cell.Index, bytes);

                _prefetch.PrefetchStorage(contract, new PaprikaKeccak(bytes));
            }
        }

        public void Commit(long blockNumber)
        {
            var keccak = _wrapped.Commit((uint)blockNumber);
            EnsurePrefetcher();
            _factory.Committed((keccak, blockNumber));
        }

        public void Reset() => _wrapped.Reset();

        public Hash256 StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }

    private void Committed((PaprikaKeccak keccak, long blockNumber) block)
    {
        const int poorManFinality = 16;

        lock (_poorManFinalizationQueue)
        {
            _poorManFinalizationQueue.Enqueue(block);

            if (_poorManFinalizationQueue.Count < poorManFinality)
            {
                // There number of ancestors is not as big as needed.
                return;
            }

            (PaprikaKeccak keccak, _) = _poorManFinalizationQueue.Dequeue();
            _blockchain.Finalize(keccak);
        }
    }
}
