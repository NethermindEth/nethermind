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
    private const int SlotPrefixLength = 30;
    private const int SlotSuffixLength = 32 - SlotPrefixLength;

    private readonly WholeReadSession _session = session;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public PerAddressEnumerable PerAddresses => new(_session.GetReader());
    public StateNodeEnumerable StateNodes => new(_snapshot, _session.GetReader());
    public StorageNodeEnumerable StorageNodes => new(_snapshot, _session.GetReader());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static NoOpPin Pin(scoped in WholeReadSessionReader reader, Bound b) =>
        reader.PinBuffer(b.Offset, b.Length);

    // ---------------- PerAddress (column 0x01: SD + Account + Slots) ----------------

    /// <summary>
    /// One row's worth of per-address data from column 0x01. The on-disk format bundles
    /// the self-destruct flag (sub-tag 0x06), account RLP (0x05), and the slot HSST
    /// (0x04) under a single per-address inner HSST, so a single outer walk yields all
    /// three sub-tags at once. The <see cref="Address"/> is materialised once per row by
    /// the enumerator and reused across sub-tag access and nested slot iteration.
    /// </summary>
    public readonly ref struct PerAddressEntry(
        WholeReadSessionReader reader, Address address, Bound slotBound, Bound accountBound, Bound sdBound)
    {
        private readonly WholeReadSessionReader _reader = reader;
        private readonly Bound _slotBound = slotBound;
        private readonly Bound _accountBound = accountBound;
        private readonly Bound _sdBound = sdBound;

        public Address Address { get; } = address;

        /// <summary>
        /// Self-destruct flag tri-state: <c>null</c> = sub-tag absent (length 0),
        /// <c>false</c> = destructed (0x00), <c>true</c> = new account marker (0x01).
        /// Matches <see cref="PersistedSnapshot.TryGetSelfDestructFlag"/> semantics.
        /// </summary>
        public bool? SelfDestructFlag
        {
            get
            {
                if (_sdBound.Length == 0) return null;
                Span<byte> tag = stackalloc byte[1];
                _reader.TryRead(_sdBound.Offset, tag);
                return tag[0] != 0x00;
            }
        }

        public bool HasAccount => _accountBound.Length > 0;

        /// <summary>
        /// Decoded account, or <c>null</c> when the on-disk marker is [0x00] (deleted) or
        /// the sub-tag is absent. Callers should branch on <see cref="HasAccount"/> first
        /// when they need to distinguish "no account update in this snapshot" from
        /// "account explicitly deleted".
        /// </summary>
        public Account? Account
        {
            get
            {
                if (_accountBound.Length == 0) return null;
                using NoOpPin pin = Pin(in _reader, _accountBound);
                ReadOnlySpan<byte> rlp = pin.Buffer;
                if (rlp.Length == 1 && rlp[0] == 0x00) return null;
                return AccountDecoder.Slim.Decode(rlp);
            }
        }

        public bool HasSlots => _slotBound.Length > 0;

        /// <summary>
        /// Nested enumerable over the slot HSST (sub-tag 0x04). Empty when <see cref="HasSlots"/>
        /// is false. The yielded <see cref="SlotEntry"/> values carry only <c>Slot</c> and
        /// <c>Value</c>; the address is on this entry and lives one foreach scope up.
        /// </summary>
        public SlotEnumerable Slots => new(_reader, _slotBound);
    }

    public readonly ref struct PerAddressEnumerable(WholeReadSessionReader reader)
    {
        private readonly WholeReadSessionReader _reader = reader;
        public PerAddressEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct PerAddressEnumerator : IDisposable
    {
        // Per-address inner DenseByteIndex tags range 0x01..0x06; pin every entry with one
        // TryResolveAll call (sized to max tag + 1 = 7). Sub-tags 0x01/0x02/0x03 only exist
        // in column 0x02 (storage trie), not here, but the dense index gap-fills them with
        // length-0 absences and we read them as such without complaint.
        private const int PerAddrSubTagCount = 7;

        private readonly WholeReadSessionReader _reader;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        // _curAddress is allocated exactly once per outer row and reused for every sub-tag
        // access and every yielded SlotEntry. Per-row cost: one 20-byte managed array plus
        // one Address object.
        private Address? _curAddress;
        private Bound _slotBound;
        private Bound _accountBound;
        private Bound _sdBound;

        public PerAddressEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out Bound matched) ? matched : default;
            _addrEnum = new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
        }

        public bool MoveNext()
        {
            Span<byte> addrBuf = stackalloc byte[Address.Size];
            Span<Bound> sub = stackalloc Bound[PerAddrSubTagCount];
            while (_addrEnum.MoveNext())
            {
                KeyValueEntry addrEntry = _addrEnum.Current;
                sub.Clear();
                HsstDenseByteIndexReader.TryResolveAll<WholeReadSessionReader, NoOpPin>(
                    in _reader, addrEntry.ValueBound, sub);
                Bound slot = sub[PersistedSnapshot.SlotSubTag[0]];
                Bound account = sub[PersistedSnapshot.AccountSubTag[0]];
                Bound sd = sub[PersistedSnapshot.SelfDestructSubTag[0]];
                // Defensive: skip rows where every sub-tag is gap-filled. The builder never
                // emits such a row, but DenseByteIndex tolerates it.
                if (slot.Length == 0 && account.Length == 0 && sd.Length == 0)
                    continue;
                ReadOnlySpan<byte> key = _addrEnum.CopyCurrentLogicalKey(addrBuf);
                _curAddress = new Address(key.ToArray());
                _slotBound = slot;
                _accountBound = account;
                _sdBound = sd;
                return true;
            }
            return false;
        }

        public readonly PerAddressEntry Current =>
            new(_reader, _curAddress!, _slotBound, _accountBound, _sdBound);

        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Slot (nested inside PerAddressEntry) ----------------

    public readonly ref struct SlotEntry(
        WholeReadSessionReader reader, ReadOnlySpan<byte> prefixKey, ReadOnlySpan<byte> suffixKey, Bound suffixValue)
    {
        private readonly WholeReadSessionReader _reader = reader;
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

    public readonly ref struct SlotEnumerable(WholeReadSessionReader reader, Bound slotBound)
    {
        private readonly WholeReadSessionReader _reader = reader;
        private readonly Bound _slotBound = slotBound;
        public SlotEnumerator GetEnumerator() => new(_reader, _slotBound);
    }

    /// <summary>
    /// Two-level walk over a per-address slot HSST: outer 30-byte prefix BTree → inner
    /// 2-byte suffix BTree. The address is supplied by the enclosing
    /// <see cref="PerAddressEntry"/>; this enumerator yields only (slot, value) pairs.
    /// </summary>
    public ref struct SlotEnumerator : IDisposable
    {
        private readonly WholeReadSessionReader _reader;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _prefixEnum;
        private HsstRefEnumerator<WholeReadSessionReader, NoOpPin> _suffixEnum;
        private byte _level; // 0=need prefix MoveNext, 1=have prefix, 2=have suffixEnum
        private readonly byte[] _curPrefix;
        private int _curPrefixLen;
        private readonly byte[] _curSuffix;
        private int _curSuffixLen;
        private Bound _curSuffixValue;

        public SlotEnumerator(WholeReadSessionReader reader, Bound slotBound)
        {
            _reader = reader;
            _curPrefix = new byte[SlotPrefixLength];
            _curSuffix = new byte[SlotSuffixLength];
            // Empty slotBound (no slots for this address) → empty enumeration.
            _prefixEnum = slotBound.Length > 0
                ? new HsstRefEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, slotBound)
                : default;
            _level = (byte)(slotBound.Length > 0 ? 1 : 0);
        }

        public bool MoveNext()
        {
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
                if (_level == 1)
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
                return false;
            }
        }

        public readonly SlotEntry Current =>
            new(_reader, _curPrefix.AsSpan(0, _curPrefixLen), _curSuffix.AsSpan(0, _curSuffixLen), _curSuffixValue);

        public void Dispose()
        {
            _suffixEnum.Dispose();
            _prefixEnum.Dispose();
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
            0 => TreePath.DecodeWith4Byte(_key),
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
            Bound b = r.TrySeek(tag, out Bound matched) ? matched : default;
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
            0 => TreePath.DecodeWith4Byte(_pathKey),
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
        // Walks column 0x02 (per-addressHash storage trie). For each address-hash we open
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
            Bound colBound = r.TrySeek(PersistedSnapshot.StorageTrieColumnTag, out Bound matched) ? matched : default;
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
