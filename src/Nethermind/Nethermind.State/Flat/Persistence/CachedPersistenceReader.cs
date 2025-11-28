// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Trie;
using NonBlocking;
using Prometheus;

namespace Nethermind.State.Flat.Persistence;

public class CachedPersistenceReader(IPersistence.IPersistenceReader innerReader): IPersistence.IPersistenceReader
{
    private static Counter _cachedReader =
        Prometheus.Metrics.CreateCounter("cached_persistence_reader_read", "reader", "type", "hit");

    private static Counter.Child _accountHit = _cachedReader.WithLabels("account", "true");
    private static Counter.Child _accountMiss = _cachedReader.WithLabels("account", "false");
    private static Counter.Child _slotHit = _cachedReader.WithLabels("slot", "true");
    private static Counter.Child _slotMiss = _cachedReader.WithLabels("slot", "false");
    private static Counter.Child _trieHit = _cachedReader.WithLabels("trie", "true");
    private static Counter.Child _trieMiss = _cachedReader.WithLabels("trie", "false");

    private ConcurrentDictionary<TrieNodeCache.Key, byte[]> _rlpCache = new();
    private ConcurrentDictionary<AddressPrefixAsKey, Account> _accountCache = new();
    private ConcurrentDictionary<(AddressPrefixAsKey, UInt256), byte[]> _slotCache = new();

    public void Dispose()
    {
        innerReader.Dispose();
    }

    public bool TryGetAccount(Address address, out Account? acc)
    {
        if (_accountCache.TryGetValue(address, out acc))
        {
            _accountHit.Inc();
            return true;
        }
        _accountMiss.Inc();

        if (innerReader.TryGetAccount(address, out acc))
        {
            _accountCache.TryAdd(address, acc);
            return true;
        }

        return false;
    }

    public bool TryGetSlot(Address address, in UInt256 index, out byte[] value)
    {
        (AddressPrefixAsKey, UInt256) key = (address, index);

        if (_slotCache.TryGetValue(key, out value))
        {
            _slotHit.Inc();
        }
        _slotMiss.Inc();

        if (innerReader.TryGetSlot(address, in index, out value))
        {
            _slotCache.TryAdd(key, value);
            return true;
        }

        return false;
    }

    public StateId CurrentState => innerReader.CurrentState;

    public byte[]? TryLoadRlp(Hash256? address, in TreePath path, Hash256 hash, ReadFlags flags)
    {
        TrieNodeCache.Key k = new TrieNodeCache.Key(address, path);
        if (_rlpCache.TryGetValue(k, out var value))
        {
            _trieHit.Inc();
            return value;
        }
        _trieMiss.Inc();

        value = innerReader.TryLoadRlp(address, in path, hash, flags);

        if (value != null)
        {
            _rlpCache.TryAdd(k, value);
        }

        return value;
    }
}
