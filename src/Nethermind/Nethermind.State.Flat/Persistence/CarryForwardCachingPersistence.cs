// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Trie;

namespace Nethermind.State.Flat.Persistence;

/// <summary>
/// <see cref="IPersistence"/> decorator that caches flat account/slot reads across heads, so a new head
/// does not re-read the serving working set from the database on its first eth_calls. Wraps the reader
/// (serve/fill) and the write batch (drops the committed write-set; clears on self-destruct or any
/// raw/range write). Generation-gated: a reader behind the cache basis bypasses it rather than serving stale data.
/// </summary>
public sealed class CarryForwardCachingPersistence : IPersistence, IAsyncDisposable
{
    private const int DefaultMaxEntriesPerKind = 262144;

    private readonly IPersistence _inner;
    private readonly int _maxEntriesPerKind;

    private readonly ConcurrentDictionary<Address, Account?> _accounts = new();
    private readonly ConcurrentDictionary<(Address, UInt256), CachedSlot> _slots = new();
    private int _accountCount;
    private int _slotCount;

    private readonly Lock _lock = new();
    private StateId _basis;
    private long _generation;
    private readonly ILogger _logger;

    // Measurement probe: per-block read classification, logged on every commit.
    private readonly ConcurrentDictionary<(Address, UInt256), byte> _mainMissedSlots = new();
    private readonly ConcurrentDictionary<Address, byte> _mainMissedAccounts = new();
    private int _slotMainHit, _slotMainMiss, _slotOtherHit, _slotOtherMiss, _slotLate, _slotBypass;
    private int _accMainHit, _accMainMiss, _accOtherHit, _accOtherMiss, _accLate, _accBypass;
    private int _clears;
    private readonly int[] _slotMainMissByPhase = new int[3];
    private readonly int[] _accMainMissByPhase = new int[3];
    private readonly int[] _slotMissByTx = new int[6];
    private readonly int[] _accMissByTx = new int[6];
    private static int TxBucket() { int i = ProcessingThread.TxIndex; return i < 0 ? 0 : i < 10 ? 1 : i < 30 ? 2 : i < 100 ? 3 : i < 300 ? 4 : 5; }
    private readonly ConcurrentDictionary<Address, int> _mainMissedSlotsByAddress = new();
    private readonly ConcurrentDictionary<Address, int> _mainMissedAccountsByAddress = new();

    public CarryForwardCachingPersistence(IPersistence inner, int maxEntriesPerKind = DefaultMaxEntriesPerKind, ILogManager? logManager = null)
    {
        _inner = inner;
        _maxEntriesPerKind = maxEntriesPerKind;
        _logger = (logManager ?? NullLogManager.Instance).GetClassLogger<CarryForwardCachingPersistence>();
        using IPersistence.IPersistenceReader reader = inner.CreateReader();
        _basis = reader.CurrentState;
    }

    public IPersistence.IPersistenceReader CreateReader(ReaderFlags flags = ReaderFlags.None)
    {
        IPersistence.IPersistenceReader reader = _inner.CreateReader(flags);
        if ((flags & ReaderFlags.Sync) != 0) return reader;

        long generation;
        bool atBasis;
        using (_lock.EnterScope())
        {
            atBasis = reader.CurrentState == _basis;
            generation = _generation;
        }
        return atBasis ? new CachingReader(this, reader, generation) : reader;
    }

    public IPersistence.IWriteBatch CreateWriteBatch(in StateId from, in StateId to, WriteFlags flags = WriteFlags.None)
        => new InvalidatingWriteBatch(this, _inner.CreateWriteBatch(from, to, flags), to);

    public void Flush() => _inner.Flush();

    public void Clear()
    {
        using (_lock.EnterScope())
        {
            ClearAllNoLock();
        }
        _inner.Clear();
    }

    public ValueTask DisposeAsync() => _inner is IAsyncDisposable asyncDisposable
        ? asyncDisposable.DisposeAsync()
        : ValueTask.CompletedTask;

    private bool IsCurrent(long readerGeneration) => Volatile.Read(ref _generation) == readerGeneration;

    private void TryCacheAccount(Address address, Account? account, long readerGeneration)
    {
        // Another reader can fill the same base miss while this reader is doing I/O.
        // The cache is best-effort, so a racing removal only loses a hint.
        if (_accounts.ContainsKey(address)) return;
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_accounts.ContainsKey(address)) return;
            if (_accountCount >= _maxEntriesPerKind)
            {
                _accounts.Clear();
                _accountCount = 0;
            }
            if (_accounts.TryAdd(address, account)) _accountCount++;
        }
    }

    private void TryCacheSlot(in (Address, UInt256) key, in CachedSlot slot, long readerGeneration)
    {
        if (_slots.ContainsKey(key)) return;
        using (_lock.EnterScope())
        {
            if (_generation != readerGeneration) return;
            if (_slots.ContainsKey(key)) return;
            if (_slotCount >= _maxEntriesPerKind)
            {
                _slots.Clear();
                _slotCount = 0;
                _clears++;
            }
            if (_slots.TryAdd(key, slot)) _slotCount++;
        }
    }

    private void RecordSlotMiss(bool main, in (Address, UInt256) key)
    {
        if (main)
        {
            Interlocked.Increment(ref _slotMainMiss);
            Interlocked.Increment(ref _slotMainMissByPhase[Math.Clamp(ProcessingThread.Phase, 0, 2)]);
            Interlocked.Increment(ref _slotMissByTx[TxBucket()]);
            _mainMissedSlots.TryAdd(key, 0);
            _mainMissedSlotsByAddress.AddOrUpdate(key.Item1, 1, static (_, c) => c + 1);
        }
        else
        {
            Interlocked.Increment(ref _slotOtherMiss);
            if (_mainMissedSlots.ContainsKey(key)) Interlocked.Increment(ref _slotLate);
        }
    }

    private void RecordAccountMiss(bool main, Address address)
    {
        if (main)
        {
            Interlocked.Increment(ref _accMainMiss);
            Interlocked.Increment(ref _accMainMissByPhase[Math.Clamp(ProcessingThread.Phase, 0, 2)]);
            Interlocked.Increment(ref _accMissByTx[TxBucket()]);
            _mainMissedAccounts.TryAdd(address, 0);
            _mainMissedAccountsByAddress.AddOrUpdate(address, 1, static (_, c) => c + 1);
        }
        else
        {
            Interlocked.Increment(ref _accOtherMiss);
            if (_mainMissedAccounts.ContainsKey(address)) Interlocked.Increment(ref _accLate);
        }
    }

    private void LogAndResetProbe(in StateId to)
    {
        try
        {
            LogAndResetProbeCore(to);
        }
        catch (Exception e)
        {
            if (_logger.IsWarn) _logger.Warn($"FlatReadProbe failed: {e}");
        }
    }

    private void LogAndResetProbeCore(in StateId to)
    {
        if (_logger.IsInfo) _logger.Info($"FlatReadProbe block={to} slots main={_slotMainHit}/{_slotMainMiss} other={_slotOtherHit}/{_slotOtherMiss} late={_slotLate} bypass={_slotBypass} acc main={_accMainHit}/{_accMainMiss} other={_accOtherHit}/{_accOtherMiss} late={_accLate} bypass={_accBypass} cache={_slotCount}/{_accountCount} clears={_clears} slotPhase={_slotMainMissByPhase[0]}/{_slotMainMissByPhase[1]}/{_slotMainMissByPhase[2]} accPhase={_accMainMissByPhase[0]}/{_accMainMissByPhase[1]}/{_accMainMissByPhase[2]} slotTx={string.Join('/', _slotMissByTx)} accTx={string.Join('/', _accMissByTx)}");
        Array.Clear(_slotMissByTx);
        Array.Clear(_accMissByTx);
        Array.Clear(_slotMainMissByPhase);
        Array.Clear(_accMainMissByPhase);
        _slotMainHit = _slotMainMiss = _slotOtherHit = _slotOtherMiss = _slotLate = _slotBypass = 0;
        _accMainHit = _accMainMiss = _accOtherHit = _accOtherMiss = _accLate = _accBypass = 0;
        _clears = 0;
        if (_logger.IsInfo) _logger.Info($"FlatReadProbeTop block={to.BlockNumber} slotAddrs={_mainMissedSlotsByAddress.Count} top={Top(_mainMissedSlotsByAddress)} accAddrs={_mainMissedAccountsByAddress.Count} top={Top(_mainMissedAccountsByAddress)}");
        _mainMissedSlots.Clear();
        _mainMissedAccounts.Clear();
        _mainMissedSlotsByAddress.Clear();
        _mainMissedAccountsByAddress.Clear();
    }

    private static string Top(ConcurrentDictionary<Address, int> counts)
    {
        // Enumerate rather than copy: other threads still add while this runs, and ICollection.CopyTo races.
        List<KeyValuePair<Address, int>> entries = [];
        foreach (KeyValuePair<Address, int> kv in counts) entries.Add(kv);
        entries.Sort(static (a, b) => b.Value.CompareTo(a.Value));
        return string.Join(",", entries.Take(8).Select(static kv => $"{kv.Key}:{kv.Value}"));
    }

    private void OnCommitted(in StateId to, HashSet<Address>? writtenAccounts, HashSet<(Address, UInt256)>? writtenSlots, bool clearAll)
    {
        LogAndResetProbe(to);
        using (_lock.EnterScope())
        {
            _generation++;
            _basis = to;

            if (clearAll)
            {
                ClearAllNoLock();
                return;
            }

            if (writtenAccounts is not null)
            {
                foreach (Address address in writtenAccounts)
                {
                    if (_accounts.TryRemove(address, out _)) _accountCount--;
                }
            }

            if (writtenSlots is not null)
            {
                foreach ((Address, UInt256) key in writtenSlots)
                {
                    if (_slots.TryRemove(key, out _)) _slotCount--;
                }
            }
        }
    }

    private void ClearAllNoLock()
    {
        _accounts.Clear();
        _accountCount = 0;
        _slots.Clear();
        _slotCount = 0;
    }

    private readonly struct CachedSlot(bool found, UInt256 value)
    {
        public readonly bool Found = found;
        public readonly UInt256 Value = value;
    }

    private sealed class CachingReader(CarryForwardCachingPersistence parent, IPersistence.IPersistenceReader inner, long generation)
        : IPersistence.IPersistenceReader
    {
        public Account? GetAccount(Address address)
        {
            bool current = parent.IsCurrent(generation);
            bool main = ProcessingThread.IsBlockProcessingThread;
            if (!current) Interlocked.Increment(ref parent._accBypass);
            if (current && parent._accounts.TryGetValue(address, out Account? cached))
            {
                Interlocked.Increment(ref main ? ref parent._accMainHit : ref parent._accOtherHit);
                return cached;
            }

            Account? account = inner.GetAccount(address);
            parent.RecordAccountMiss(main, address);
            if (current) parent.TryCacheAccount(address, account, generation);
            return account;
        }

        public bool TryGetSlot(Address address, in UInt256 slot, ref UInt256 outValue)
        {
            (Address, UInt256) key = (address, slot);
            bool current = parent.IsCurrent(generation);
            bool main = ProcessingThread.IsBlockProcessingThread;
            if (!current) Interlocked.Increment(ref parent._slotBypass);
            if (current && parent._slots.TryGetValue(key, out CachedSlot cached))
            {
                Interlocked.Increment(ref main ? ref parent._slotMainHit : ref parent._slotOtherHit);
                if (cached.Found) outValue = cached.Value;
                return cached.Found;
            }

            bool found = inner.TryGetSlot(address, slot, ref outValue);
            parent.RecordSlotMiss(main, key);
            if (current) parent.TryCacheSlot(key, new CachedSlot(found, found ? outValue : default), generation);
            return found;
        }

        public StateId CurrentState => inner.CurrentState;
        public byte[]? TryLoadStateRlp(in TreePath path, ReadFlags flags) => inner.TryLoadStateRlp(path, flags);
        public byte[]? TryLoadStorageRlp(Hash256 address, in TreePath path, ReadFlags flags) => inner.TryLoadStorageRlp(address, path, flags);
        public byte[]? GetAccountRaw(in ValueHash256 addrHash) => inner.GetAccountRaw(addrHash);
        public bool TryGetStorageRaw(in ValueHash256 addrHash, in ValueHash256 slotHash, ref UInt256 value) => inner.TryGetStorageRaw(addrHash, slotHash, ref value);
        public IPersistence.IFlatIterator CreateAccountIterator(in ValueHash256 startKey, in ValueHash256 endKey) => inner.CreateAccountIterator(startKey, endKey);
        public IPersistence.IFlatIterator CreateStorageIterator(in ValueHash256 accountKey, in ValueHash256 startSlotKey, in ValueHash256 endSlotKey) => inner.CreateStorageIterator(accountKey, startSlotKey, endSlotKey);
        public bool IsPreimageMode => inner.IsPreimageMode;
        public void Dispose() => inner.Dispose();
    }

    private sealed class InvalidatingWriteBatch(CarryForwardCachingPersistence parent, IPersistence.IWriteBatch inner, StateId to)
        : IPersistence.IWriteBatch
    {
        private HashSet<Address>? _writtenAccounts;
        private HashSet<(Address, UInt256)>? _writtenSlots;
        private bool _clearAll;

        public void SelfDestruct(Address addr)
        {
            _clearAll = true;
            inner.SelfDestruct(addr);
        }

        public void SetAccount(Address addr, Account? account)
        {
            (_writtenAccounts ??= []).Add(addr);
            inner.SetAccount(addr, account);
        }

        public void SetStorage(Address addr, in UInt256 slot, in UInt256? value)
        {
            (_writtenSlots ??= []).Add((addr, slot));
            inner.SetStorage(addr, slot, value);
        }

        public void SetStorageRawEncoded(in ValueHash256 addrHash, in ValueHash256 slotHash, scoped ReadOnlySpan<byte> rlpValue)
        {
            _clearAll = true;
            inner.SetStorageRawEncoded(addrHash, slotHash, rlpValue);
        }

        public void SetAccountRaw(in ValueHash256 addrHash, Account account)
        {
            _clearAll = true;
            inner.SetAccountRaw(addrHash, account);
        }

        public void DeleteAccountRange(in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            _clearAll = true;
            inner.DeleteAccountRange(fromPath, toPath);
        }

        public void DeleteStorageRange(in ValueHash256 addressHash, in ValueHash256 fromPath, in ValueHash256 toPath)
        {
            _clearAll = true;
            inner.DeleteStorageRange(addressHash, fromPath, toPath);
        }

        public void SetStateTrieNode(in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStateTrieNode(path, rlp);
        public void SetStorageTrieNode(Hash256 address, in TreePath path, scoped ReadOnlySpan<byte> rlp) => inner.SetStorageTrieNode(address, path, rlp);
        public void DeleteStateTrieNodeRange(in ValueHash256 from, in ValueHash256 to) => inner.DeleteStateTrieNodeRange(from, to);
        public void DeleteStorageTrieNodeRange(in ValueHash256 addressHash, in ValueHash256 from, in ValueHash256 to) => inner.DeleteStorageTrieNodeRange(addressHash, from, to);

        public void Dispose()
        {
            inner.Dispose();
            parent.OnCommitted(to, _writtenAccounts, _writtenSlots, _clearAll);
        }
    }
}
