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
/// <see cref="WholeReadSession"/> guarantees the underlying view stays valid for the
/// scanner's lifetime; enumerators address it via a <see cref="WholeReadSessionReader"/>
/// and pin individual key/value byte ranges on demand. Each entry yielded by an
/// enumerator stores only the raw <see cref="Bound"/>s; key and value are decoded
/// lazily on property access — consumers that read only one side never pay for
/// the other.
/// </summary>
public sealed class PersistedSnapshotScanner(WholeReadSession session, PersistedSnapshot snapshot)
{
    private const int SlotPrefixLength = 31;

    private readonly WholeReadSession _session = session;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public SelfDestructEnumerable SelfDestructedStorageAddresses => new(_session.GetReader());
    public AccountEnumerable Accounts => new(_session.GetReader());
    public StorageEnumerable Storages => new(_session.GetReader());
    public StateNodeEnumerable StateNodes => new(_snapshot, _session.GetReader());
    public StorageNodeEnumerable StorageNodes => new(_snapshot, _session.GetReader());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NoOpPin Pin(scoped in WholeReadSessionReader reader, Bound b) =>
        reader.PinBuffer(b.Offset, b.Length);

    // ---------------- SelfDestruct ----------------

    public readonly ref struct SelfDestructEntry(WholeReadSessionReader reader, ReadOnlySpan<byte> key, Bound value)
    {
        private readonly WholeReadSessionReader _reader = reader;
        private readonly ReadOnlySpan<byte> _key = key;
        private readonly Bound _value = value;
        public ValueHash256 AddressHash
        {
            get
            {
                ValueHash256 h = default;
                _key.CopyTo(h.BytesAsSpan[.._key.Length]);
                return h;
            }
        }
        public bool IsNew
        {
            get
            {
                if (_value.Length == 0) return false;
                Span<byte> tag = stackalloc byte[1];
                _reader.TryRead(_value.Offset, tag);
                return tag[0] == 0x01;
            }
        }
    }

    public readonly ref struct SelfDestructEnumerable(WholeReadSessionReader reader)
    {
        private readonly WholeReadSessionReader _reader = reader;
        public readonly SelfDestructEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct SelfDestructEnumerator : IDisposable
    {
        private readonly WholeReadSessionReader _reader;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        // Address-hash key copied here in logical form; HsstRefEnumerator hides whether
        // the source PackedArray is LE-stored. 32 covers the 20-byte address hash with
        // headroom.
        private readonly byte[] _curKey;
        private int _curKeyLen;
        private Bound _curValue;

        public SelfDestructEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            _curKey = new byte[32];
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
        }

        public bool MoveNext()
        {
            while (_addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = _addrEnum.Current;
                HsstReader<WholeReadSessionReader, NoOpPin> perAddr = new(in _reader, addrEntry.ValueBound);
                // DenseByteIndex returns success even for gap-filled (length 0) absent
                // entries; only yield addresses with an actual SD record (length > 0).
                if (!perAddr.TrySeek(PersistedSnapshot.SelfDestructSubTag, out _))
                    continue;
                Bound sdBound = perAddr.GetBound();
                if (sdBound.Length == 0)
                    continue;
                _curKeyLen = _addrEnum.CopyCurrentLogicalKey(_curKey).Length;
                _curValue = sdBound;
                return true;
            }
            return false;
        }

        public readonly SelfDestructEntry Current => new(_reader, _curKey.AsSpan(0, _curKeyLen), _curValue);
        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Account ----------------

    public readonly ref struct AccountEntry(WholeReadSessionReader reader, ReadOnlySpan<byte> key, Bound rlp)
    {
        private readonly WholeReadSessionReader _reader = reader;
        private readonly ReadOnlySpan<byte> _key = key;
        private readonly Bound _rlp = rlp;
        public ValueHash256 AddressHash
        {
            get
            {
                ValueHash256 h = default;
                _key.CopyTo(h.BytesAsSpan[.._key.Length]);
                return h;
            }
        }
        public Account? Account
        {
            get
            {
                // Presence-marker encoding: [0x00] = deleted (null), RLP-bytes = present.
                // The enumerator already filters length-0 absences before yielding.
                using NoOpPin pin = Pin(in _reader, _rlp);
                ReadOnlySpan<byte> rlp = pin.Buffer;
                if (rlp.Length == 1 && rlp[0] == 0x00) return null;
                return AccountDecoder.Slim.Decode(rlp);
            }
        }
    }

    public readonly ref struct AccountEnumerable(WholeReadSessionReader reader)
    {
        private readonly WholeReadSessionReader _reader = reader;
        public readonly AccountEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct AccountEnumerator : IDisposable
    {
        private readonly WholeReadSessionReader _reader;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        // Address-hash key copied here in logical form. 32 covers the 20-byte hash.
        private readonly byte[] _curKey;
        private int _curKeyLen;
        private Bound _curRlp;

        public AccountEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            _curKey = new byte[32];
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
        }

        public bool MoveNext()
        {
            while (_addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = _addrEnum.Current;
                HsstReader<WholeReadSessionReader, NoOpPin> perAddr = new(in _reader, addrEntry.ValueBound);
                // DenseByteIndex returns success even for gap-filled (length 0) absent
                // entries; only yield addresses with an actual account record (length > 0).
                if (!perAddr.TrySeek(PersistedSnapshot.AccountSubTag, out _))
                    continue;
                Bound rlpBound = perAddr.GetBound();
                if (rlpBound.Length == 0)
                    continue;
                _curKeyLen = _addrEnum.CopyCurrentLogicalKey(_curKey).Length;
                _curRlp = rlpBound;
                return true;
            }
            return false;
        }

        public readonly AccountEntry Current => new(_reader, _curKey.AsSpan(0, _curKeyLen), _curRlp);
        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Storage ----------------

    public readonly ref struct StorageEntry(
        WholeReadSessionReader reader, ValueHash256 addressHash, ReadOnlySpan<byte> prefixKey, ReadOnlySpan<byte> suffixKey, Bound suffixValue)
    {
        private readonly WholeReadSessionReader _reader = reader;
        public ValueHash256 AddressHash { get; } = addressHash;
        private readonly ReadOnlySpan<byte> _prefix = prefixKey;
        private readonly ReadOnlySpan<byte> _suffix = suffixKey;
        private readonly Bound _value = suffixValue;
        public UInt256 Slot
        {
            get
            {
                Span<byte> slotKey = stackalloc byte[32];
                _prefix.CopyTo(slotKey[.._prefix.Length]);
                _suffix.CopyTo(slotKey[SlotPrefixLength..]);
                return new UInt256(slotKey, isBigEndian: true);
            }
        }
        public SlotValue? Value
        {
            get
            {
                if (_value.Length == 0) return null;
                using NoOpPin pin = Pin(in _reader, _value);
                return SlotValue.FromSpanWithoutLeadingZero(pin.Buffer);
            }
        }
    }

    public readonly ref struct StorageEnumerable(WholeReadSessionReader reader)
    {
        private readonly WholeReadSessionReader _reader = reader;
        public readonly StorageEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct StorageEnumerator : IDisposable
    {
        private readonly WholeReadSessionReader _reader;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _prefixEnum;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _suffixEnum;
        private byte _level; // 0=need new addr, 1=have prefixEnum, 2=have suffixEnum
        private ValueHash256 _curAddrHash;
        // Slot prefix is 31 bytes (BTree, not LE-stored), slot suffix is 1 byte (ByteTagMap).
        // Logical-form copies; HsstRefEnumerator hides any LE-stored layout.
        private readonly byte[] _curPrefix;
        private int _curPrefixLen;
        private readonly byte[] _curSuffix;
        private int _curSuffixLen;
        private Bound _curSuffixValue;

        public StorageEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            _curPrefix = new byte[SlotPrefixLength];
            _curSuffix = new byte[1];
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
            _level = 0;
            _curAddrHash = default;
        }

        public bool MoveNext()
        {
            Span<byte> hashBuf = stackalloc byte[32];
            while (true)
            {
                if (_level >= 2)
                {
                    if (_suffixEnum.MoveNext())
                    {
                        _curSuffixLen = _suffixEnum.CopyCurrentLogicalKey(_curSuffix).Length;
                        _curSuffixValue = _suffixEnum.Current.ValueBound;
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
                        _curPrefixLen = _prefixEnum.CopyCurrentLogicalKey(_curPrefix).Length;
                        _suffixEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, _prefixEnum.Current.ValueBound);
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
                HsstReader<WholeReadSessionReader, NoOpPin> perAddr = new(in _reader, addrEntry.ValueBound);
                if (!perAddr.TrySeek(PersistedSnapshot.SlotSubTag, out _))
                    continue;
                Bound slotBound = perAddr.GetBound();
                // DenseByteIndex returns success even for gap-filled (length 0) absences;
                // skip addresses that have other sub-tags but no slots.
                if (slotBound.Length == 0)
                    continue;
                // Hash is repeated across many slots; decode eagerly once per address-hash
                // by zero-padding the 20-byte column key into a ValueHash256 (struct, no
                // alloc).
                _curAddrHash = default;
                ReadOnlySpan<byte> hashKey = _addrEnum.CopyCurrentLogicalKey(hashBuf);
                hashKey.CopyTo(_curAddrHash.BytesAsSpan[..hashKey.Length]);
                _prefixEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, slotBound);
                _level = 1;
            }
        }

        public readonly StorageEntry Current =>
            new(_reader, _curAddrHash, _curPrefix.AsSpan(0, _curPrefixLen), _curSuffix.AsSpan(0, _curSuffixLen), _curSuffixValue);

        public void Dispose()
        {
            _suffixEnum.Dispose();
            _prefixEnum.Dispose();
            _addrEnum.Dispose();
        }
    }

    // ---------------- StateNode ----------------

    public readonly ref struct StateNodeEntry(
        PersistedSnapshot snapshot, WholeReadSessionReader reader, ReadOnlySpan<byte> key, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly WholeReadSessionReader _reader = reader;
        private readonly ReadOnlySpan<byte> _key = key;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path => _stage switch
        {
            0 => TreePath.DecodeWith3Byte(_key),
            1 => PersistedSnapshotReader.DecodeCompactTreePath(_key),
            _ => new(new ValueHash256(_key[..32]), _key[32]),
        };
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveTrieRlp(_value);
    }

    public readonly ref struct StateNodeEnumerable(PersistedSnapshot snapshot, WholeReadSessionReader reader)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly WholeReadSessionReader _reader = reader;
        public StateNodeEnumerator GetEnumerator() => new(_snapshot, _reader);
    }

    public ref struct StateNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly WholeReadSessionReader _reader;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _inner;
        private byte _stage; // 0=TopNodes, 1=CompactNodes, 2=Fallback, 3=done
        // State-trie path key in logical form. Stage 1 (compact, keySize=8) is auto
        // LE-stored at the source; CopyCurrentLogicalKey un-reverses it. 33 covers the
        // largest path encoding (fallback hash+nibble).
        private readonly byte[] _curKey;
        private int _curKeyLen;
        private Bound _curValue;

        public StateNodeEnumerator(PersistedSnapshot snapshot, WholeReadSessionReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _curKey = new byte[33];
            _stage = 0;
            _inner = OpenColumn(in _reader, PersistedSnapshot.StateTopNodesTag);
        }

        private static HsstRefEnumerator<WholeReadSessionReader, NoOpPin> OpenColumn(scoped in WholeReadSessionReader reader, byte[] tag)
        {
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in reader);
            Bound b = r.TrySeek(tag, out _) ? r.GetBound() : default;
            return new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in reader, b);
        }

        public bool MoveNext()
        {
            while (_stage < 3)
            {
                if (_inner.MoveNext())
                {
                    _curKeyLen = _inner.CopyCurrentLogicalKey(_curKey).Length;
                    _curValue = _inner.Current.ValueBound;
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

        public readonly StateNodeEntry Current => new(_snapshot, _reader, _curKey.AsSpan(0, _curKeyLen), _curValue, _stage);
        public void Dispose() => _inner.Dispose();
    }

    // ---------------- StorageNode ----------------

    public readonly ref struct StorageNodeEntry(
        PersistedSnapshot snapshot, WholeReadSessionReader reader, ValueHash256 addressHash,
        ReadOnlySpan<byte> pathKey, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly WholeReadSessionReader _reader = reader;
        public ValueHash256 AddressHash { get; } = addressHash;
        private readonly ReadOnlySpan<byte> _pathKey = pathKey;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path => _stage switch
        {
            0 => TreePath.DecodeWith3Byte(_pathKey),
            1 => PersistedSnapshotReader.DecodeCompactTreePath(_pathKey),
            _ => new(new ValueHash256(_pathKey[..32]), _pathKey[32]),
        };
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveTrieRlp(_value);
    }

    public readonly ref struct StorageNodeEnumerable(PersistedSnapshot snapshot, WholeReadSessionReader reader)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly WholeReadSessionReader _reader = reader;
        public StorageNodeEnumerator GetEnumerator() => new(_snapshot, _reader);
    }

    public ref struct StorageNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly WholeReadSessionReader _reader;
        // Walks the unified column 0x01 (per-address). For each address-hash we open
        // the inner storage-trie sub-tags in order: top (0x01), compact (0x02), then
        // fallback (0x03).
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _pathEnum;
        // _stage: 0 = current address-hash's top sub-tag, 1 = its compact sub-tag,
        // 2 = its fallback sub-tag. Reported back to StorageNodeEntry for path-key
        // decoding (top 3 bytes / compact 8 bytes / fallback 33 bytes), so it doubles
        // as the on-disk path-encoding selector.
        private byte _stage;
        private byte _level;  // 0=need new addr, 1=have pathEnum
        private Bound _addrInnerBound;
        private ValueHash256 _curHash;
        // Path key in logical form. Stage 1 (compact, keySize=8) is auto LE-stored at the
        // source; CopyCurrentLogicalKey un-reverses. 33 covers the largest path encoding.
        private readonly byte[] _curPathKey;
        private int _curPathKeyLen;
        private Bound _curValue;

        public StorageNodeEnumerator(PersistedSnapshot snapshot, WholeReadSessionReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _curPathKey = new byte[33];
            _stage = 0;
            _level = 0;
            _curHash = default;
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
        }

        private static bool TryOpenSubTag(
            scoped in WholeReadSessionReader reader, Bound addrInner, byte[] subTag,
            out HsstRefEnumerator<WholeReadSessionReader, NoOpPin> e)
        {
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in reader, addrInner);
            if (!r.TrySeek(subTag, out _))
            {
                e = default;
                return false;
            }
            Bound b = r.GetBound();
            // DenseByteIndex returns success on gap-filled absences; treat length 0 as
            // "this sub-tag is empty" so we don't pay an enumerator setup for nothing.
            if (b.Length == 0)
            {
                e = default;
                return false;
            }
            e = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in reader, b);
            return true;
        }

        public bool MoveNext()
        {
            Span<byte> hashBuf = stackalloc byte[32];
            while (true)
            {
                if (_level == 1)
                {
                    if (_pathEnum.MoveNext())
                    {
                        _curPathKeyLen = _pathEnum.CopyCurrentLogicalKey(_curPathKey).Length;
                        _curValue = _pathEnum.Current.ValueBound;
                        return true;
                    }
                    _pathEnum.Dispose();
                    _pathEnum = default;
                    // Advance through the storage sub-tag chain: top → compact → fallback.
                    if (_stage == 0)
                    {
                        _stage = 1;
                        if (TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageCompactSubTag, out _pathEnum))
                            continue;
                    }
                    if (_stage == 1)
                    {
                        _stage = 2;
                        if (TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageFallbackSubTag, out _pathEnum))
                            continue;
                    }
                    _level = 0;
                    _stage = 0;
                }
                // _level == 0: pull next address that has at least one storage sub-tag.
                if (!_addrEnum.MoveNext()) return false;
                KeyValueEntry addrEntry = _addrEnum.Current;
                _addrInnerBound = addrEntry.ValueBound;
                _stage = 0;
                if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageTopSubTag, out _pathEnum))
                {
                    _stage = 1;
                    if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageCompactSubTag, out _pathEnum))
                    {
                        _stage = 2;
                        if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageFallbackSubTag, out _pathEnum))
                            continue;
                    }
                }
                _curHash = default;
                ReadOnlySpan<byte> hashKey = _addrEnum.CopyCurrentLogicalKey(hashBuf);
                hashKey.CopyTo(_curHash.BytesAsSpan[..hashKey.Length]);
                _level = 1;
            }
        }

        public readonly StorageNodeEntry Current =>
            new(_snapshot, _reader, _curHash, _curPathKey.AsSpan(0, _curPathKeyLen), _curValue, _stage);

        public void Dispose()
        {
            _pathEnum.Dispose();
            _addrEnum.Dispose();
        }
    }
}
