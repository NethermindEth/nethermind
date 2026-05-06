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
/// scanner's lifetime, so enumerators slice keys/values directly out of it. Each entry
/// yielded by an enumerator stores only the raw <see cref="Bound"/>s; key and value are
/// decoded lazily on property access — consumers that read only one side never pay for
/// the other.
/// </summary>
public sealed class PersistedSnapshotScanner(WholeReadSession session, PersistedSnapshot snapshot)
{
    private const int SlotPrefixLength = 31;

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

    // ---------------- SelfDestruct ----------------

    public readonly ref struct SelfDestructEntry(ReadOnlySpan<byte> data, Bound key, Bound value)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly Bound _key = key;
        private readonly Bound _value = value;
        public Address Address => new(Slice(_data, _key));
        public bool IsNew => _value.Length > 0 && _data[(int)_value.Offset] == 0x01;
    }

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
        private Bound _curKey;
        private Bound _curValue;

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
                // DenseByteIndex returns success even for gap-filled (length 0) absent
                // entries; only yield addresses with an actual SD record (length > 0).
                if (!perAddr.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
                    continue;
                Bound sdBound = perAddr.GetBound();
                if (sdBound.Length == 0)
                    continue;
                _curKey = addrEntry.KeyBound;
                _curValue = sdBound;
                return true;
            }
            return false;
        }

        public readonly SelfDestructEntry Current => new(_data, _curKey, _curValue);
        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Account ----------------

    public readonly ref struct AccountEntry(ReadOnlySpan<byte> data, Bound key, Bound rlp)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly Bound _key = key;
        private readonly Bound _rlp = rlp;
        public Address Address => new(Slice(_data, _key));
        public Account? Account
        {
            get
            {
                // Presence-marker encoding: [0x00] = deleted (null), RLP-bytes = present.
                // The enumerator already filters length-0 absences before yielding.
                ReadOnlySpan<byte> rlp = Slice(_data, _rlp);
                if (rlp.Length == 1 && rlp[0] == 0x00) return null;
                return AccountDecoder.Slim.Decode(rlp);
            }
        }
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
        private Bound _curKey;
        private Bound _curRlp;

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
                // DenseByteIndex returns success even for gap-filled (length 0) absent
                // entries; only yield addresses with an actual account record (length > 0).
                if (!perAddr.TrySeek(PersistedSnapshot.AccountSubTag, out _))
                    continue;
                Bound rlpBound = perAddr.GetBound();
                if (rlpBound.Length == 0)
                    continue;
                _curKey = addrEntry.KeyBound;
                _curRlp = rlpBound;
                return true;
            }
            return false;
        }

        public readonly AccountEntry Current => new(_data, _curKey, _curRlp);
        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Storage ----------------

    public readonly ref struct StorageEntry(
        ReadOnlySpan<byte> data, Address address, Bound prefixKey, Bound suffixKey, Bound suffixValue)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        public Address Address { get; } = address;
        private readonly Bound _prefix = prefixKey;
        private readonly Bound _suffix = suffixKey;
        private readonly Bound _value = suffixValue;
        public UInt256 Slot
        {
            get
            {
                Span<byte> slotKey = stackalloc byte[32];
                Slice(_data, _prefix).CopyTo(slotKey);
                Slice(_data, _suffix).CopyTo(slotKey[SlotPrefixLength..]);
                return new UInt256(slotKey, isBigEndian: true);
            }
        }
        public SlotValue? Value
        {
            get
            {
                ReadOnlySpan<byte> raw = Slice(_data, _value);
                return raw.IsEmpty ? null : SlotValue.FromSpanWithoutLeadingZero(raw);
            }
        }
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
        private Bound _curPrefix;
        private Bound _curSuffixKey;
        private Bound _curSuffixValue;

        public StorageEnumerator(ReadOnlySpan<byte> data)
        {
            _data = data;
            _reader = new SpanByteReader(data);
            HsstReader<SpanByteReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, colBound);
            _level = 0;
            _curAddr = default!;
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
                        _curSuffixKey = suffixEntry.KeyBound;
                        _curSuffixValue = suffixEntry.ValueBound;
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
                        _curPrefix = prefixEntry.KeyBound;
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
                // Address is decoded eagerly (once per address) since it's repeated
                // across many slots; a single Address alloc per address is the right shape.
                _curAddr = new Address(Slice(_data, addrEntry.KeyBound));
                _prefixEnum = new HsstEnumerator<SpanByteReader, NoOpPin>(in _reader, perAddr.GetBound());
                _level = 1;
            }
        }

        public readonly StorageEntry Current =>
            new(_data, _curAddr, _curPrefix, _curSuffixKey, _curSuffixValue);

        public void Dispose()
        {
            _suffixEnum.Dispose();
            _prefixEnum.Dispose();
            _addrEnum.Dispose();
        }
    }

    // ---------------- StateNode ----------------

    public readonly ref struct StateNodeEntry(
        PersistedSnapshot snapshot, ReadOnlySpan<byte> data, Bound key, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _data = data;
        private readonly Bound _key = key;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path
        {
            get
            {
                ReadOnlySpan<byte> k = Slice(_data, _key);
                return _stage switch
                {
                    0 => TreePath.DecodeWith3Byte(k),
                    1 => PersistedSnapshotReader.DecodeCompactTreePath(k),
                    _ => new(new ValueHash256(k[..32]), k[32]),
                };
            }
        }
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveValueAt(_value);
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
        private Bound _curKey;
        private Bound _curValue;

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
                    _curKey = entry.KeyBound;
                    _curValue = entry.ValueBound;
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

        public readonly StateNodeEntry Current => new(_snapshot, _data, _curKey, _curValue, _stage);
        public void Dispose() => _inner.Dispose();
    }

    // ---------------- StorageNode ----------------

    public readonly ref struct StorageNodeEntry(
        PersistedSnapshot snapshot, ReadOnlySpan<byte> data, Hash256 addressHash,
        Bound pathKey, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _data = data;
        public Hash256 AddressHash { get; } = addressHash;
        private readonly Bound _pathKey = pathKey;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path
        {
            get
            {
                ReadOnlySpan<byte> k = Slice(_data, _pathKey);
                return _stage == 0
                    ? PersistedSnapshotReader.DecodeCompactTreePath(k)
                    : new(new ValueHash256(k[..32]), k[32]);
            }
        }
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveValueAt(_value);
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
        private Bound _curPathKey;
        private Bound _curValue;

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
                        _curPathKey = pathEntry.KeyBound;
                        _curValue = pathEntry.ValueBound;
                        return true;
                    }
                    _pathEnum.Dispose();
                    _pathEnum = default;
                    _level = 0;
                }
                if (_hashEnum.MoveNext())
                {
                    KeyValueEntry hashEntry = _hashEnum.Current;
                    // Hash is repeated across many path entries; decode eagerly per hash.
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

        public readonly StorageNodeEntry Current =>
            new(_snapshot, _data, _curHash, _curPathKey, _curValue, _stage);

        public void Dispose()
        {
            _pathEnum.Dispose();
            _hashEnum.Dispose();
        }
    }
}
