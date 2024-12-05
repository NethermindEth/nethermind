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
using Paprika.Data;
using Paprika.Merkle;
using Paprika.Store;
using Account = Nethermind.Core.Account;
using IRawState = Paprika.Chain.IRawState;
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
    private readonly Queue<(PaprikaKeccak keccak, uint number)> _poorManFinalizationQueue = new();
    private uint _lastFinalized;
    private readonly ReaderWriterLockSlim _commitLock = new();

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

    public IState Get(Hash256 stateRoot, bool prefetchMerkle)
    {
        return new State(_blockchain.StartNew(Convert(stateRoot)), this, prefetchMerkle);
    }
    
    public Nethermind.State.IRawState GetRaw() => new RawState(_blockchain.StartRaw(), this);
    public Nethermind.State.IRawState GetRaw(ValueHash256 rootHash) => new RawState(_blockchain.StartRaw(Convert(rootHash)), this);

    public IReadOnlyState GetReadOnly(Hash256? stateRoot) =>
        new ReadOnlyState(stateRoot != null
            ? _blockchain.StartReadOnly(Convert(stateRoot))
            : _blockchain.StartReadOnlyLatestFromDb());

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
    
    public void ForceFlush()
    {
        _blockchain.ForceFlush();
    }

    public void ResetAccessor()
    {
        _accessor.Reset();
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
    
    public void AquireRawStateCommitLock()
    {
        _commitLock.EnterWriteLock();
    }

    public void ReleaseRawStateCommitLock()
    {
        _commitLock.ExitWriteLock();
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
        private readonly IPreCommitPrefetcher? _prefetch;
        private readonly HashSet<int> _prefetched;

        public State(IWorldState wrapped, PaprikaStateFactory factory, bool prefetchMerkle)
        {
            _wrapped = wrapped;
            _factory = factory;
            _prefetch = prefetchMerkle && factory.Prefetch ? _wrapped.OpenPrefetcher() : null;
            _prefetched = new HashSet<int>();
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
            _wrapped.Commit((uint)blockNumber);
            _factory.Committed(_wrapped);
        }

        public void Reset() => _wrapped.Reset();

        public Hash256 StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }
    
    class RawState : Nethermind.State.IRawState
    {
        private readonly IRawState _wrapped;
        private readonly PaprikaStateFactory _factory;

        public RawState(IRawState wrapped, PaprikaStateFactory factory)
        {
            _wrapped = wrapped;
            _factory = factory;
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

        public bool TryGet(Address address, out AccountStruct account)
        {
            PaprikaAccount retrieved = _wrapped.GetAccount(Convert(address));
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

        public ReadOnlySpan<byte> GetStorageAt(scoped in StorageCell cell)
        {
            Span<byte> bytes = stackalloc byte[32];
            GetKey(cell.Index, bytes);

            bytes = _wrapped.GetStorage(Convert(cell.Address), new PaprikaKeccak(bytes), bytes);
            return MaterializeStorageValue(bytes);
        }

        [SkipLocalsInit]
        public ReadOnlySpan<byte> GetStorageAt(Address address, in ValueHash256 hash)
        {
            Span<byte> bytes = stackalloc byte[32];
            bytes = _wrapped.GetStorage(Convert(address), new PaprikaKeccak(hash.Bytes), bytes);
            return MaterializeStorageValue(bytes);
        }

        [SkipLocalsInit]
        public ReadOnlySpan<byte> GetStorageAt(in ValueHash256 accountHash, in ValueHash256 hash)
        {
            Span<byte> bytes = stackalloc byte[32];
            bytes = _wrapped.GetStorage(new PaprikaKeccak(accountHash.Bytes), new PaprikaKeccak(hash.Bytes), bytes);
            return MaterializeStorageValue(bytes);
        }

        [SkipLocalsInit]
        public void SetStorage(in StorageCell cell, ReadOnlySpan<byte> value)
        {
            Span<byte> key = stackalloc byte[32];
            GetKey(cell.Index, key);
            PaprikaKeccak converted = Convert(cell.Address);
            _wrapped.SetStorage(converted, new PaprikaKeccak(key), value);
        }

        public void SetStorage(ValueHash256 accountHash, ValueHash256 storageSlotHash, ReadOnlySpan<byte> encodedValue)
        {
            PaprikaKeccak addressKey = Convert(accountHash);
            PaprikaKeccak storageKey = Convert(storageSlotHash);
            _wrapped.SetStorage(addressKey, storageKey, encodedValue);
        }

        public void SetAccount(ValueHash256 hash, Account? account)
        {
            PaprikaKeccak key = Convert(hash);

            if (account is null)
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

        public void CreateProofBranch(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength, byte[] childNibbles, Hash256?[] childHashes, bool persist = true)
        {
            NibblePath path = NibblePath.FromKey(keyPath).SliceTo(targetKeyLength);
            PaprikaKeccak[] hashes = new PaprikaKeccak[childHashes.Length];
            for (int i = 0; i < childHashes.Length; i++)
            {
                hashes[i] = childHashes[i] is not null ? Convert(childHashes[i]!) : PaprikaKeccak.Zero;
            }
            _wrapped.CreateMerkleBranch(Convert(accountHash), path, childNibbles, hashes, persist);
        }

        public void CreateProofExtension(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength,
            int extPathLength, bool persist = true)
        {
            var fullPath = NibblePath.FromKey(keyPath);
            NibblePath storagePath = fullPath.SliceTo(targetKeyLength);
            NibblePath extensionPath = fullPath.SliceFrom(targetKeyLength).SliceTo(extPathLength);

            _wrapped.CreateMerkleExtension(Convert(accountHash), storagePath, extensionPath, persist);
        }

        public void CreateProofLeaf(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength, int leafKeyIndex)
        {
            var fullPath = NibblePath.FromKey(keyPath);
            NibblePath storagePath = fullPath.SliceTo(targetKeyLength);
            NibblePath leafPath = fullPath.SliceFrom(targetKeyLength);

            _wrapped.CreateMerkleLeaf(Convert(accountHash), storagePath, leafPath);
        }

        public void RegisterDeleteByPrefix(ValueHash256 accountHash, ReadOnlySpan<byte> keyPath, int targetKeyLength)
        {
            var fullPath = NibblePath.FromKey(keyPath).SliceTo(targetKeyLength);
            Key key = accountHash == Keccak.Zero
                ? Key.Account(fullPath)
                : Key.StorageCell(NibblePath.FromKey(Convert(accountHash)), fullPath);
            _wrapped.RegisterDeleteByPrefix(key);
        }

        public void Commit(bool ensureHash)
        {
            try
            {
                _factory.AquireRawStateCommitLock();
                _wrapped.Commit(ensureHash);
            }
            finally
            {
                _factory.ReleaseRawStateCommitLock();
            }
        }

        public ValueHash256 GetHash(ReadOnlySpan<byte> path, int pathLength, bool ignoreCache)
        {
            NibblePath nibblePath = NibblePath.FromKey(path).SliceTo(pathLength);
            return Convert(_wrapped.GetHash(nibblePath, ignoreCache));
        }

        public ValueHash256 GetStorageHash(ValueHash256 accountHash, ReadOnlySpan<byte> storagePath, int pathLength, bool ignoreCache)
        {
            NibblePath nibblePath = NibblePath.FromKey(storagePath).SliceTo(pathLength);
            return Convert(_wrapped.GetStorageHash(Convert(accountHash), nibblePath, ignoreCache));
        }

        public bool IsPersisted(ValueHash256 accountHash, ReadOnlySpan<byte> path, int pathLength)
        {
            NibblePath nibblePath = NibblePath.FromKey(path).SliceTo(pathLength);
            return _wrapped.IsPersisted(Convert(accountHash), nibblePath);
        }

        public ValueHash256 RecalculateRootHash()
        {
            return Convert(_wrapped.RecalculateRootHash());
        }

        public void Finalize(uint blockNumber)
        {
            try
            {
                _factory.AquireRawStateCommitLock();
                _wrapped.Finalize(blockNumber);
            }
            finally
            {
                _factory.ReleaseRawStateCommitLock();
            }
        }

        public string DumpTrie()
        {
            return _wrapped.DumpTrie();
        }

        public ValueHash256 RefreshRootHash()
        {
            return Convert(_wrapped.RefreshRootHash());
        }

        public ValueHash256 RecalculateStorageRoot(ValueHash256 accountHash)
        {
            PaprikaKeccak account = Convert(accountHash);
            return Convert(_wrapped.RecalculateStorageRoot(account));
        }

        public void Discard()
        {
            _wrapped.Discard();
        }

        public void ProcessProofNodes(ValueHash256 accountHash, Span<byte> packedProofPaths, int proofCount)
        {
            PaprikaKeccak account = Convert(accountHash);
            _wrapped.ProcessProofNodes(account, packedProofPaths, proofCount);
        }

        public Hash256 StateRoot => Convert(_wrapped.Hash);

        public void Dispose() => _wrapped.Dispose();
    }

    private void Committed(IWorldState block)
    {
        const int poorManFinality = 16;

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
