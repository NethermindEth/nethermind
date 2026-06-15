// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;
using Nethermind.State.Flat.Hsst.DenseByteIndex;

namespace Nethermind.State.Flat.PersistedSnapshots;

/// <summary>
/// Non-generic entry points for <see cref="PersistedSnapshotScanner{TSource,TReader,TPin}"/>.
/// </summary>
public static class PersistedSnapshotScanner
{
    /// <summary>
    /// A scanner reading through a <see cref="WholeReadSession"/>'s whole-buffer mmap view. The
    /// caller owns the session lifetime — it must outlive the returned scanner and any enumerator
    /// derived from it.
    /// </summary>
    public static PersistedSnapshotScanner<WholeReadSession, WholeReadSessionReader, NoOpPin> ForWholeRead(
        WholeReadSession session, PersistedSnapshot snapshot) =>
        new(session, snapshot);
}

/// <summary>
/// Streaming scan over a persisted snapshot's HSST columns, generic over the byte-reader source so
/// the traversal isn't bound to a specific reader. The <typeparamref name="TSource"/> (held as a
/// value) mints a fresh <typeparamref name="TReader"/> per enumerator; the caller guarantees the
/// underlying region stays valid for the scanner's lifetime. Each entry yielded by an enumerator
/// stores only the raw <see cref="Bound"/>s; key and value are decoded lazily on property access —
/// consumers that read only one side never pay for the other.
/// </summary>
public sealed class PersistedSnapshotScanner<TSource, TReader, TPin>(TSource source, PersistedSnapshot snapshot)
    where TSource : IHsstReaderSource<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private const int SlotPrefixLength = 30;
    private const int SlotSuffixLength = 32 - SlotPrefixLength;

    private readonly TSource _source = source;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public PerAddressEnumerable PerAddresses => new(_source.CreateReader());
    public StateNodeEnumerable StateNodes => new(_snapshot, _source.CreateReader());
    public StorageNodeEnumerable StorageNodes => new(_snapshot, _source.CreateReader());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TPin Pin(scoped in TReader reader, Bound b) =>
        reader.PinBuffer(b);

    // ---------------- PerAddress (column 0x01: Account + SD + Slots) ----------------

    /// <summary>
    /// One row's worth of per-address data from column 0x01. The on-disk format keys this
    /// column by raw 20-byte Address; the inner DenseByteIndex carries sub-tags 0x00 (account),
    /// 0x01 (self-destruct), 0x02 (slots). Storage-trie nodes live in column 0x05 keyed
    /// by addressHash and are surfaced via <see cref="StorageNodes"/>.
    /// </summary>
    public readonly ref struct PerAddressEntry(
        TReader reader, Address address,
        Bound slotBound, Bound accountBound, Bound sdBound)
    {
        private readonly TReader _reader = reader;
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
                return tag[0] != PersistedSnapshotTags.SelfDestructDestructedMarkerByte;
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
                using TPin pin = Pin(in _reader, _accountBound);
                ReadOnlySpan<byte> rlp = pin.Buffer;
                if (rlp.Length == 1 && rlp[0] == PersistedSnapshotTags.AccountDeletedMarkerByte) return null;
                return AccountDecoder.Slim.Decode(rlp);
            }
        }

        /// <summary>
        /// Nested enumerable over the slot HSST (sub-tag 0x02). Empty when the slot sub-tag
        /// is absent. The yielded <see cref="SlotEntry"/> values carry only <c>Slot</c> and
        /// <c>Value</c>; the address is on this entry and lives one foreach scope up.
        /// </summary>
        public SlotEnumerable Slots => new(_reader, _slotBound);
    }

    public readonly ref struct PerAddressEnumerable(TReader reader)
    {
        private readonly TReader _reader = reader;
        public PerAddressEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct PerAddressEnumerator : IDisposable
    {
        private readonly TReader _reader;
        private HsstEnumerator<TReader, TPin> _addrEnum;
        // _curAddress is materialised once per outer row from the 20-byte outer key and
        // reused across every sub-tag access and yielded SlotEntry. Per-row cost: one
        // Address object plus its backing 20-byte array.
        private Address? _curAddress;
        private Bound _slotBound;
        private Bound _accountBound;
        private Bound _sdBound;

        public PerAddressEnumerator(TReader reader)
        {
            _reader = reader;
            HsstReader<TReader, TPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshotTags.AccountColumnTag, out Bound matched) ? matched : default;
            _addrEnum = new HsstEnumerator<TReader, TPin>(in _reader, colBound);
        }

        public bool MoveNext()
        {
            Span<byte> addrBuf = stackalloc byte[PersistedSnapshotTags.AddressKeyLength];
            Span<Bound> sub = stackalloc Bound[PersistedSnapshotTags.PerAddrSubTagCount];
            while (_addrEnum.MoveNext(in _reader))
            {
                Bound addrInner = _addrEnum.CurrentValue;
                sub.Clear();
                HsstDenseByteIndexReader.TryResolveAll<TReader, TPin>(
                    in _reader, addrInner, sub);
                Bound slot = sub[PersistedSnapshotTags.SlotSubTagByte];
                Bound account = sub[PersistedSnapshotTags.AccountSubTagByte];
                Bound sd = sub[PersistedSnapshotTags.SelfDestructSubTagByte];
                // Defensive: skip rows where every sub-tag is gap-filled.
                if (slot.Length == 0 && account.Length == 0 && sd.Length == 0)
                    continue;
                ReadOnlySpan<byte> addrKey = _addrEnum.CopyCurrentLogicalKey(in _reader, addrBuf);
                _curAddress = new Address(addrKey);
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
        TReader reader, ReadOnlySpan<byte> prefixKey, ReadOnlySpan<byte> suffixKey, Bound suffixValue)
    {
        private readonly TReader _reader = reader;
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
                using TPin pin = Pin(in _reader, _value);
                // Present values are RLP-wrapped byte-strings; unwrap before reconstruction.
                ReadOnlySpan<byte> value = new Rlp.ValueDecoderContext(pin.Buffer).DecodeByteArraySpan();
                return SlotValue.FromSpanWithoutLeadingZero(value);
            }
        }
    }

    public readonly ref struct SlotEnumerable(TReader reader, Bound slotBound)
    {
        private readonly TReader _reader = reader;
        private readonly Bound _slotBound = slotBound;
        public SlotEnumerator GetEnumerator() => new(_reader, _slotBound);
    }

    /// <summary>
    /// Two-level walk over a per-address slot HSST: outer 30-byte prefix BTreeKeyFirst →
    /// inner 2-byte suffix keys-first TwoByteSlotValue / -Large blob. The address is
    /// supplied by the enclosing <see cref="PerAddressEntry"/>; this enumerator yields
    /// only (slot, value) pairs.
    /// </summary>
    public ref struct SlotEnumerator : IDisposable
    {
        private readonly TReader _reader;
        private HsstEnumerator<TReader, TPin> _prefixEnum;
        private HsstEnumerator<TReader, TPin> _suffixEnum;
        private byte _level; // 0=need prefix MoveNext, 1=have prefix, 2=have suffixEnum
        private readonly byte[] _curPrefix;
        private int _curPrefixLen;
        private readonly byte[] _curSuffix;
        private int _curSuffixLen;
        private Bound _curSuffixValue;

        public SlotEnumerator(TReader reader, Bound slotBound)
        {
            _reader = reader;
            _curPrefix = new byte[SlotPrefixLength];
            _curSuffix = new byte[SlotSuffixLength];
            // Empty slotBound (no slots for this address) → empty enumeration.
            _prefixEnum = slotBound.Length > 0
                ? new HsstEnumerator<TReader, TPin>(in _reader, slotBound)
                : default;
            _level = (byte)(slotBound.Length > 0 ? 1 : 0);
        }

        public bool MoveNext()
        {
            while (true)
            {
                if (_level >= 2)
                {
                    if (_suffixEnum.MoveNext(in _reader))
                    {
                        _curSuffixLen = _suffixEnum.CopyCurrentLogicalKey(in _reader, _curSuffix).Length;
                        _curSuffixValue = _suffixEnum.CurrentValue;
                        return true;
                    }
                    _suffixEnum.Dispose();
                    _suffixEnum = default;
                    _level = 1;
                }
                if (_level == 1)
                {
                    if (_prefixEnum.MoveNext(in _reader))
                    {
                        _curPrefixLen = _prefixEnum.CopyCurrentLogicalKey(in _reader, _curPrefix).Length;
                        // The prefix entry's value is a keys-first TwoByteSlotValue / -Large
                        // sub-slot blob — front-dispatch on byte 0, no tail read.
                        _suffixEnum = HsstEnumerator<TReader, TPin>.CreateTwoByteSlot(
                            in _reader, _prefixEnum.CurrentValue);
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
        PersistedSnapshot snapshot, ReadOnlySpan<byte> key, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _key = key;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path => _stage switch
        {
            0 => TreePath.DecodeWith4Byte(_key),
            1 => TreePath.DecodeWith8Byte(_key),
            _ => new(new ValueHash256(_key[..32]), _key[32]),
        };
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveTrieRlp(_value);
    }

    public readonly ref struct StateNodeEnumerable(PersistedSnapshot snapshot, TReader reader)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly TReader _reader = reader;
        public StateNodeEnumerator GetEnumerator() => new(_snapshot, _reader);
    }

    public ref struct StateNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly TReader _reader;
        private HsstEnumerator<TReader, TPin> _inner;
        private byte _stage; // 0=TopNodes, 1=CompactNodes, 2=Fallback, 3=done
        // State-trie path key in logical form. Stage 1 (compact, keySize=8) is auto
        // LE-stored at the source; CopyCurrentLogicalKey un-reverses it. 33 covers the
        // largest path encoding (fallback hash+nibble).
        private readonly byte[] _curKey;
        private int _curKeyLen;
        private Bound _curValue;

        public StateNodeEnumerator(PersistedSnapshot snapshot, TReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _curKey = new byte[33];
            _stage = 0;
            _inner = OpenColumn(in _reader, PersistedSnapshotTags.StateTopNodesTag);
        }

        private static HsstEnumerator<TReader, TPin> OpenColumn(scoped in TReader reader, byte[] tag)
        {
            HsstReader<TReader, TPin> r = new(in reader);
            Bound b = r.TrySeek(tag, out Bound matched) ? matched : default;
            return new HsstEnumerator<TReader, TPin>(in reader, b);
        }

        public bool MoveNext()
        {
            while (_stage < 3)
            {
                if (_inner.MoveNext(in _reader))
                {
                    _curKeyLen = _inner.CopyCurrentLogicalKey(in _reader, _curKey).Length;
                    _curValue = _inner.CurrentValue;
                    return true;
                }
                _inner.Dispose();
                _stage++;
                _inner = _stage switch
                {
                    1 => OpenColumn(in _reader, PersistedSnapshotTags.StateNodeTag),
                    2 => OpenColumn(in _reader, PersistedSnapshotTags.StateNodeFallbackTag),
                    _ => default,
                };
            }
            return false;
        }

        public readonly StateNodeEntry Current => new(_snapshot, _curKey.AsSpan(0, _curKeyLen), _curValue, _stage);
        public void Dispose() => _inner.Dispose();
    }

    // ---------------- StorageNode ----------------

    public readonly ref struct StorageNodeEntry(
        PersistedSnapshot snapshot, ValueHash256 addressHash,
        ReadOnlySpan<byte> pathKey, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        public ValueHash256 AddressHash { get; } = addressHash;
        private readonly ReadOnlySpan<byte> _pathKey = pathKey;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path => _stage switch
        {
            0 => TreePath.DecodeWith4Byte(_pathKey),
            1 => TreePath.DecodeWith8Byte(_pathKey),
            _ => new(new ValueHash256(_pathKey[..32]), _pathKey[32]),
        };
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveTrieRlp(_value);
    }

    public readonly ref struct StorageNodeEnumerable(PersistedSnapshot snapshot, TReader reader)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly TReader _reader = reader;
        public StorageNodeEnumerator GetEnumerator() => new(_snapshot, _reader);
    }

    public ref struct StorageNodeEnumerator : IDisposable
    {
        private readonly PersistedSnapshot _snapshot;
        private readonly TReader _reader;
        // Walks column 0x05 (storage-trie) keyed by addressHash. For each row we open the
        // storage-trie sub-tags in order: top (0x00), compact (0x01), then fallback (0x02).
        private HsstEnumerator<TReader, TPin> _addrEnum;
        private HsstEnumerator<TReader, TPin> _pathEnum;
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

        public StorageNodeEnumerator(PersistedSnapshot snapshot, TReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _curPathKey = new byte[33];
            _stage = 0;
            _level = 0;
            _curHash = default;
            HsstReader<TReader, TPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshotTags.StorageTrieColumnTag, out Bound matched) ? matched : default;
            _addrEnum = new HsstEnumerator<TReader, TPin>(in _reader, colBound);
        }

        private static bool TryOpenSubTag(
            scoped in TReader reader, Bound addrInner, byte[] subTag,
            out HsstEnumerator<TReader, TPin> e)
        {
            HsstReader<TReader, TPin> r = new(in reader, addrInner);
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
            e = new HsstEnumerator<TReader, TPin>(in reader, b);
            return true;
        }

        public bool MoveNext()
        {
            Span<byte> hashBuf = stackalloc byte[32];
            while (true)
            {
                if (_level == 1)
                {
                    if (_pathEnum.MoveNext(in _reader))
                    {
                        _curPathKeyLen = _pathEnum.CopyCurrentLogicalKey(in _reader, _curPathKey).Length;
                        _curValue = _pathEnum.CurrentValue;
                        return true;
                    }
                    _pathEnum.Dispose();
                    _pathEnum = default;
                    // Advance through the storage sub-tag chain: top → compact → fallback.
                    if (_stage == 0)
                    {
                        _stage = 1;
                        if (TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshotTags.StorageCompactSubTag, out _pathEnum))
                            continue;
                    }
                    if (_stage == 1)
                    {
                        _stage = 2;
                        if (TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshotTags.StorageFallbackSubTag, out _pathEnum))
                            continue;
                    }
                    _level = 0;
                    _stage = 0;
                }
                // _level == 0: pull next address that has at least one storage sub-tag.
                if (!_addrEnum.MoveNext(in _reader)) return false;
                _addrInnerBound = _addrEnum.CurrentValue;
                _stage = 0;
                if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshotTags.StorageTopSubTag, out _pathEnum))
                {
                    _stage = 1;
                    if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshotTags.StorageCompactSubTag, out _pathEnum))
                    {
                        _stage = 2;
                        if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshotTags.StorageFallbackSubTag, out _pathEnum))
                            continue;
                    }
                }
                _curHash = default;
                ReadOnlySpan<byte> hashKey = _addrEnum.CopyCurrentLogicalKey(in _reader, hashBuf);
                hashKey.CopyTo(_curHash.BytesAsSpan[..hashKey.Length]);
                _level = 1;
            }
        }

        public readonly StorageNodeEntry Current =>
            new(_snapshot, _curHash, _curPathKey.AsSpan(0, _curPathKeyLen), _curValue, _stage);

        public void Dispose()
        {
            _pathEnum.Dispose();
            _addrEnum.Dispose();
        }
    }
}
