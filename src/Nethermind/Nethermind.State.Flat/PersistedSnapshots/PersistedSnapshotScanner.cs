// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.Storage;
using Nethermind.Trie;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Streaming scan over a persisted snapshot's HSST columns. The
/// <see cref="WholeReadSession"/> guarantees the underlying span stays valid for the
/// scanner's lifetime, so enumerators slice keys/values directly out of it instead of
/// copying through TryRead. Each enumerable re-walks the B-tree on iteration — fine for
/// one-shot consumers (RocksDB flush) and acceptable for the bloom builder's two-pass
/// count + populate.
/// </summary>
public sealed class PersistedSnapshotScanner(WholeReadSession session, PersistedSnapshot snapshot)
{
    private const int SlotPrefixLength = 30;

    private readonly WholeReadSession _session = session;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public SelfDestructEnumerable SelfDestructedStorageAddresses => new(_session.GetSpan());
    public AccountEnumerable Accounts => new(_session.GetSpan());
    public StorageEnumerable Storages => new(_session.GetSpan());
    public StateNodeEnumerable StateNodes => new(_snapshot, _session.GetSpan());
    public StorageNodeEnumerable StorageNodes => new(_snapshot, _session.GetSpan());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> data, Bound b) =>
        data.Slice((int)b.Offset, b.Length);

    public readonly ref struct SelfDestructEnumerable(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public readonly SelfDestructEnumerator GetEnumerator() => new(_data);
    }

    public ref struct SelfDestructEnumerator : IDisposable
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _addrEnum;
        private KeyValuePair<AddressAsKey, bool> _current;

        public SelfDestructEnumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _reader = new SpanByteReader(data);
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
                Address addr = new(Slice(_data, addrEntry.KeyBound));
                bool isNew = sdBound.Length > 0 && _data[(int)sdBound.Offset] == 0x01;
                _current = new(addr, isNew);
                return true;
            }
            return false;
        }

        public readonly KeyValuePair<AddressAsKey, bool> Current => _current;
        public void Dispose() => _addrEnum.Dispose();
    }

    public readonly ref struct AccountEnumerable(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public readonly AccountEnumerator GetEnumerator() => new(_data);
    }

    public ref struct AccountEnumerator : IDisposable
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _addrEnum;
        private KeyValuePair<AddressAsKey, Account?> _current;

        public AccountEnumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _reader = new SpanByteReader(data);
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
                Address addr = new(Slice(_data, addrEntry.KeyBound));
                ReadOnlySpan<byte> rlp = Slice(_data, rlpBound);
                Account? account = rlp.IsEmpty ? null : AccountDecoder.Slim.Decode(rlp);
                _current = new(addr, account);
                return true;
            }
            return false;
        }

        public readonly KeyValuePair<AddressAsKey, Account?> Current => _current;
        public void Dispose() => _addrEnum.Dispose();
    }

    public readonly ref struct StorageEnumerable(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public readonly StorageEnumerator GetEnumerator() => new(_data);
    }

    public ref struct StorageEnumerator : IDisposable
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _addrEnum;
        private HsstEnumerator<SpanByteReader, NoOpPin> _prefixEnum;
        private HsstEnumerator<SpanByteReader, NoOpPin> _suffixEnum;
        private byte _level; // 0=need new addr, 1=have prefixEnum, 2=have suffixEnum
        private Address _curAddr;
        private Bound _curPrefixBound;
        private KeyValuePair<(AddressAsKey, UInt256), SlotValue?> _current;

        public StorageEnumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _reader = new SpanByteReader(data);
            HsstReader<SpanByteReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, colBound);
            _level = 0;
            _curAddr = default!;
            _curPrefixBound = default;
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
                        Slice(_data, _curPrefixBound).CopyTo(slotKey);
                        Slice(_data, suffixEntry.KeyBound).CopyTo(slotKey[SlotPrefixLength..]);
                        UInt256 slot = new(slotKey, isBigEndian: true);
                        ReadOnlySpan<byte> raw = Slice(_data, suffixEntry.ValueBound);
                        SlotValue? value = raw.IsEmpty ? null : SlotValue.FromSpanWithoutLeadingZero(raw);
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
                        _curPrefixBound = prefixEntry.KeyBound;
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
                _curAddr = new Address(Slice(_data, addrEntry.KeyBound));
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

    public readonly ref struct StateNodeEnumerable(PersistedSnapshot snapshot, ReadOnlySpan<byte> data)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _data = data;
        public StateNodeEnumerator GetEnumerator() => new(_snapshot, _data);
    }

    public ref struct StateNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly ReadOnlySpan<byte> _data;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _inner;
        private byte _stage; // 0=TopNodes, 1=CompactNodes, 2=Fallback, 3=done
        private KeyValuePair<TreePath, TrieNode> _current;

        public StateNodeEnumerator(PersistedSnapshot snapshot, ReadOnlySpan<byte> data)
        {
            _snapshot = snapshot;
            _data = data;
            _reader = new SpanByteReader(data);
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
                    ReadOnlySpan<byte> key = Slice(_data, entry.KeyBound);
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

    public readonly ref struct StorageNodeEnumerable(PersistedSnapshot snapshot, ReadOnlySpan<byte> data)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _data = data;
        public StorageNodeEnumerator GetEnumerator() => new(_snapshot, _data);
    }

    public ref struct StorageNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly ReadOnlySpan<byte> _data;
        private readonly SpanByteReader _reader;
        private HsstEnumerator<SpanByteReader, NoOpPin> _hashEnum;
        private HsstEnumerator<SpanByteReader, NoOpPin> _pathEnum;
        private byte _stage;  // 0=Compact column, 1=Fallback column, 2=done
        private byte _level;  // 0=need new hash, 1=have pathEnum
        private Hash256 _curHash;
        private KeyValuePair<(Hash256AsKey, TreePath), TrieNode> _current;

        public StorageNodeEnumerator(PersistedSnapshot snapshot, ReadOnlySpan<byte> data)
        {
            _snapshot = snapshot;
            _data = data;
            _reader = new SpanByteReader(data);
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
            Span<byte> hashKeyPadded = stackalloc byte[32];
            while (_stage < 2)
            {
                if (_level == 1)
                {
                    if (_pathEnum.MoveNext())
                    {
                        KeyValueEntry pathEntry = _pathEnum.Current;
                        ReadOnlySpan<byte> key = Slice(_data, pathEntry.KeyBound);
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
                    hashKeyPadded.Clear();
                    Slice(_data, hashEntry.KeyBound).CopyTo(hashKeyPadded);
                    _curHash = new Hash256(hashKeyPadded);
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
