// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Streaming scan over a persisted snapshot's HSST columns. Pair the snapshot with a
/// <see cref="WholeReadSession"/> taken on its reservation; the scanner does not own the
/// session but enumerators are only valid while the session is alive. Each enumerable
/// re-walks the B-tree on iteration — fine for one-shot consumers (e.g. RocksDB flush)
/// and acceptable for the bloom builder's two-pass count + populate.
/// </summary>
public sealed class PersistedSnapshotScanner(WholeReadSession session, PersistedSnapshot snapshot)
{
    private const int SlotPrefixLength = 30;

    private readonly WholeReadSession _session = session;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public SelfDestructEnumerable SelfDestructedStorageAddresses => new(_snapshot);
    public AccountEnumerable Accounts => new(_snapshot);
    public StorageEnumerable Storages => new(_snapshot);
    public StateNodeEnumerable StateNodes => new(_snapshot);
    public StorageNodeEnumerable StorageNodes => new(_snapshot);

    public readonly ref struct SelfDestructEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public readonly SelfDestructEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct SelfDestructEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _addrEnum;
        private KeyValuePair<AddressAsKey, bool> _current;

        public SelfDestructEnumerator(PersistedSnapshot snapshot)
        {
            _snapshot = snapshot;
            _reader = snapshot.CreateReader();
            HsstReader<SpanByteReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, colBound);
        }

        public bool MoveNext()
        {
            while (_addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = _addrEnum.Current;
                HsstReader<SpanByteReader, NoOpPin> perAddr = new(in _reader, addrEntry.ValueBound);
                if (!perAddr.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
                    continue;
                Bound sdBound = perAddr.GetBound();
                byte[] addrBytes = new byte[addrEntry.KeyBound.Length];
                _reader.TryRead(addrEntry.KeyBound.Offset, addrBytes);
                bool isNew = false;
                if (sdBound.Length > 0)
                {
                    Span<byte> oneByte = stackalloc byte[1];
                    _reader.TryRead(sdBound.Offset, oneByte);
                    isNew = oneByte[0] == 0x01;
                }
                _current = new(new Address(addrBytes), isNew);
                return true;
            }
            return false;
        }

        public readonly KeyValuePair<AddressAsKey, bool> Current => _current;
        public void Dispose() => _addrEnum.Dispose();
    }

    public readonly ref struct AccountEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public readonly AccountEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct AccountEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _addrEnum;
        private KeyValuePair<AddressAsKey, Account?> _current;

        public AccountEnumerator(PersistedSnapshot snapshot)
        {
            _snapshot = snapshot;
            _reader = snapshot.CreateReader();
            HsstReader<SpanByteReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, colBound);
        }

        public bool MoveNext()
        {
            while (_addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = _addrEnum.Current;
                HsstReader<SpanByteReader, NoOpPin> perAddr = new(in _reader, addrEntry.ValueBound);
                if (!perAddr.TrySeek(PersistedSnapshot.AccountSubTag, out _))
                    continue;
                Bound rlpBound = perAddr.GetBound();
                byte[] addrBytes = new byte[addrEntry.KeyBound.Length];
                _reader.TryRead(addrEntry.KeyBound.Offset, addrBytes);
                Account? account;
                if (rlpBound.Length == 0)
                {
                    account = null;
                }
                else
                {
                    Span<byte> rlpBuf = rlpBound.Length <= 256 ? stackalloc byte[256] : new byte[rlpBound.Length];
                    Span<byte> rlp = rlpBuf[..rlpBound.Length];
                    _reader.TryRead(rlpBound.Offset, rlp);
                    account = AccountDecoder.Slim.Decode(rlp);
                }
                _current = new(new Address(addrBytes), account);
                return true;
            }
            return false;
        }

        public readonly KeyValuePair<AddressAsKey, Account?> Current => _current;
        public void Dispose() => _addrEnum.Dispose();
    }

    public readonly ref struct StorageEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public readonly StorageEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StorageEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _addrEnum;
        private HsstEnumerator<SpanByteReader, NoOpPin> _prefixEnum;
        private HsstEnumerator<SpanByteReader, NoOpPin> _suffixEnum;
        private byte _level; // 0=need new addr, 1=have prefixEnum, 2=have suffixEnum
        private Address _curAddr;
        private byte[] _curPrefixBytes;
        private KeyValuePair<(AddressAsKey, UInt256), SlotValue?> _current;

        public StorageEnumerator(PersistedSnapshot snapshot)
        {
            _snapshot = snapshot;
            _reader = snapshot.CreateReader();
            HsstReader<SpanByteReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, colBound);
            _level = 0;
            _curAddr = default!;
            _curPrefixBytes = [];
        }

        public bool MoveNext()
        {
            while (true)
            {
                if (_level >= 2)
                {
                    if (_suffixEnum.MoveNext())
                    {
                        KeyValueEntry suffixEntry = _suffixEnum.Current;
                        Span<byte> slotKey = stackalloc byte[32];
                        _curPrefixBytes.CopyTo(slotKey);
                        _reader.TryRead(suffixEntry.KeyBound.Offset, slotKey.Slice(SlotPrefixLength, suffixEntry.KeyBound.Length));
                        UInt256 slot = new(slotKey, isBigEndian: true);
                        SlotValue? value;
                        if (suffixEntry.ValueBound.Length == 0)
                        {
                            value = null;
                        }
                        else
                        {
                            Span<byte> vbuf = stackalloc byte[32];
                            Span<byte> v = vbuf[..suffixEntry.ValueBound.Length];
                            _reader.TryRead(suffixEntry.ValueBound.Offset, v);
                            value = SlotValue.FromSpanWithoutLeadingZero(v);
                        }
                        _current = new((_curAddr, slot), value);
                        return true;
                    }
                    _suffixEnum.Dispose();
                    _suffixEnum = default;
                    _level = 1;
                }
                if (_level >= 1)
                {
                    if (_prefixEnum.MoveNext())
                    {
                        KeyValueEntry prefixEntry = _prefixEnum.Current;
                        _curPrefixBytes = new byte[prefixEntry.KeyBound.Length];
                        _reader.TryRead(prefixEntry.KeyBound.Offset, _curPrefixBytes);
                        _suffixEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, prefixEntry.ValueBound);
                        _level = 2;
                        continue;
                    }
                    _prefixEnum.Dispose();
                    _prefixEnum = default;
                    _level = 0;
                }
                // _level == 0: pull next address that has SlotSubTag
                if (!_addrEnum.MoveNext()) return false;
                KeyValueEntry addrEntry = _addrEnum.Current;
                HsstReader<SpanByteReader, NoOpPin> perAddr = new(in _reader, addrEntry.ValueBound);
                if (!perAddr.TrySeek(PersistedSnapshot.SlotSubTag, out _))
                    continue;
                byte[] addrBytes = new byte[addrEntry.KeyBound.Length];
                _reader.TryRead(addrEntry.KeyBound.Offset, addrBytes);
                _curAddr = new Address(addrBytes);
                _prefixEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, perAddr.GetBound());
                _level = 1;
            }
        }

        public readonly KeyValuePair<(AddressAsKey, UInt256), SlotValue?> Current => _current;

        public void Dispose()
        {
            _suffixEnum.Dispose();
            _prefixEnum.Dispose();
            _addrEnum.Dispose();
        }
    }

    public readonly struct StateNodeEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public StateNodeEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StateNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _inner;
        private byte _stage; // 0=TopNodes, 1=CompactNodes, 2=Fallback, 3=done
        private KeyValuePair<TreePath, TrieNode> _current;

        public StateNodeEnumerator(PersistedSnapshot snapshot)
        {
            _snapshot = snapshot;
            _reader = snapshot.CreateReader();
            _stage = 0;
            _inner = OpenColumn(in _reader, PersistedSnapshot.StateTopNodesTag);
        }

        private static HsstEnumerator<SpanByteReader, NoOpPin> OpenColumn(scoped in SpanByteReader reader, byte[] tag)
        {
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Bound b = r.TrySeek(tag, out _) ? r.GetBound() : default;
            return new HsstEnumerator<SpanByteReader, NoOpPin>(in reader, b);
        }

        public bool MoveNext()
        {
            while (_stage < 3)
            {
                if (_inner.MoveNext())
                {
                    KeyValueEntry entry = _inner.Current;
                    Span<byte> keyBuf = stackalloc byte[33];
                    Span<byte> key = keyBuf[..entry.KeyBound.Length];
                    _reader.TryRead(entry.KeyBound.Offset, key);
                    TreePath path = _stage switch
                    {
                        0 => TreePath.DecodeWith3Byte(key),
                        1 => PersistedSnapshotReader.DecodeCompactTreePath(key),
                        _ => new(new ValueHash256(key[..32]), key[32]),
                    };
                    byte[] valueBytes = _snapshot.ResolveValueAt(entry.ValueBound);
                    _current = new(path, new TrieNode(NodeType.Unknown, valueBytes));
                    return true;
                }
                _inner.Dispose();
                _stage++;
                _inner = _stage switch
                {
                    1 => OpenColumn(in _reader, PersistedSnapshot.StateNodeTag),
                    2 => OpenColumn(in _reader, PersistedSnapshot.StateNodeFallbackTag),
                    _ => default,
                };
            }
            return false;
        }

        public readonly KeyValuePair<TreePath, TrieNode> Current => _current;
        public void Dispose() => _inner.Dispose();
    }

    public readonly struct StorageNodeEnumerable(PersistedSnapshot snapshot)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public StorageNodeEnumerator GetEnumerator() => new(_snapshot);
    }

    public ref struct StorageNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _hashEnum;
        private HsstEnumerator<SpanByteReader, NoOpPin> _pathEnum;
        private byte _stage;  // 0=Compact column, 1=Fallback column, 2=done
        private byte _level;  // 0=need new hash, 1=have pathEnum
        private Hash256 _curHash;
        private KeyValuePair<(Hash256AsKey, TreePath), TrieNode> _current;

        public StorageNodeEnumerator(PersistedSnapshot snapshot)
        {
            _snapshot = snapshot;
            _reader = snapshot.CreateReader();
            _stage = 0;
            _level = 0;
            _curHash = default!;
            _hashEnum = OpenColumn(in _reader, PersistedSnapshot.StorageNodeTag);
        }

        private static HsstEnumerator<SpanByteReader, NoOpPin> OpenColumn(scoped in SpanByteReader reader, byte[] tag)
        {
            HsstReader<SpanByteReader, NoOpPin> r = new(in reader);
            Bound b = r.TrySeek(tag, out _) ? r.GetBound() : default;
            return new HsstEnumerator<SpanByteReader, NoOpPin>(in reader, b);
        }

        public bool MoveNext()
        {
            while (_stage < 2)
            {
                if (_level == 1)
                {
                    if (_pathEnum.MoveNext())
                    {
                        KeyValueEntry pathEntry = _pathEnum.Current;
                        Span<byte> keyBuf = stackalloc byte[33];
                        Span<byte> key = keyBuf[..pathEntry.KeyBound.Length];
                        _reader.TryRead(pathEntry.KeyBound.Offset, key);
                        TreePath path = _stage == 0
                            ? PersistedSnapshotReader.DecodeCompactTreePath(key)
                            : new(new ValueHash256(key[..32]), key[32]);
                        byte[] valueBytes = _snapshot.ResolveValueAt(pathEntry.ValueBound);
                        _current = new((_curHash, path), new TrieNode(NodeType.Unknown, valueBytes));
                        return true;
                    }
                    _pathEnum.Dispose();
                    _pathEnum = default;
                    _level = 0;
                }
                if (_hashEnum.MoveNext())
                {
                    KeyValueEntry hashEntry = _hashEnum.Current;
                    byte[] hashBytes = new byte[32];
                    _reader.TryRead(hashEntry.KeyBound.Offset, hashBytes.AsSpan(0, hashEntry.KeyBound.Length));
                    _curHash = new Hash256(hashBytes);
                    _pathEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, hashEntry.ValueBound);
                    _level = 1;
                    continue;
                }
                _hashEnum.Dispose();
                _stage++;
                _hashEnum = _stage == 1
                    ? OpenColumn(in _reader, PersistedSnapshot.StorageNodeFallbackTag)
                    : default;
            }
            return false;
        }

        public readonly KeyValuePair<(Hash256AsKey, TreePath), TrieNode> Current => _current;

        public void Dispose()
        {
            _pathEnum.Dispose();
            _hashEnum.Dispose();
        }
    }
}
