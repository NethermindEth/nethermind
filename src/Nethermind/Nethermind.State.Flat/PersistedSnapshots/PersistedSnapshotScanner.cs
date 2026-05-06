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

    public readonly ref struct SelfDestructEntry(WholeReadSessionReader reader, Bound key, Bound value)
    {
        private readonly WholeReadSessionReader _reader = reader;
        private readonly Bound _key = key;
        private readonly Bound _value = value;
        public Hash256 AddressHash
        {
            get
            {
                Span<byte> padded = stackalloc byte[32];
                using NoOpPin pin = Pin(in _reader, _key);
                pin.Buffer.CopyTo(padded);
                return new Hash256(padded);
            }
        }
        public bool IsNew
        {
            get
            {
                if (_value.Length == 0) return false;
                using NoOpPin pin = _reader.PinBuffer(_value.Offset, 1);
                return pin.Buffer[0] == 0x01;
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
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        private Bound _curKey;
        private Bound _curValue;

        public SelfDestructEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
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
                _curKey = addrEntry.KeyBound;
                _curValue = sdBound;
                return true;
            }
            return false;
        }

        public readonly SelfDestructEntry Current => new(_reader, _curKey, _curValue);
        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Account ----------------

    public readonly ref struct AccountEntry(WholeReadSessionReader reader, Bound key, Bound rlp)
    {
        private readonly WholeReadSessionReader _reader = reader;
        private readonly Bound _key = key;
        private readonly Bound _rlp = rlp;
        public Hash256 AddressHash
        {
            get
            {
                Span<byte> padded = stackalloc byte[32];
                using NoOpPin pin = Pin(in _reader, _key);
                pin.Buffer.CopyTo(padded);
                return new Hash256(padded);
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
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        private Bound _curKey;
        private Bound _curRlp;

        public AccountEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
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
                _curKey = addrEntry.KeyBound;
                _curRlp = rlpBound;
                return true;
            }
            return false;
        }

        public readonly AccountEntry Current => new(_reader, _curKey, _curRlp);
        public void Dispose() => _addrEnum.Dispose();
    }

    // ---------------- Storage ----------------

    public readonly ref struct StorageEntry(
        WholeReadSessionReader reader, Hash256 addressHash, Bound prefixKey, Bound suffixKey, Bound suffixValue)
    {
        private readonly WholeReadSessionReader _reader = reader;
        public Hash256 AddressHash { get; } = addressHash;
        private readonly Bound _prefix = prefixKey;
        private readonly Bound _suffix = suffixKey;
        private readonly Bound _value = suffixValue;
        public UInt256 Slot
        {
            get
            {
                Span<byte> slotKey = stackalloc byte[32];
                using (NoOpPin prefixPin = Pin(in _reader, _prefix))
                    prefixPin.Buffer.CopyTo(slotKey);
                using (NoOpPin suffixPin = Pin(in _reader, _suffix))
                    suffixPin.Buffer.CopyTo(slotKey[SlotPrefixLength..]);
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
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _prefixEnum;
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _suffixEnum;
        private byte _level; // 0=need new addr, 1=have prefixEnum, 2=have suffixEnum
        private Hash256 _curAddrHash;
        private Bound _curPrefix;
        private Bound _curSuffixKey;
        private Bound _curSuffixValue;

        public StorageEnumerator(WholeReadSessionReader reader)
        {
            _reader = reader;
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
            _level = 0;
            _curAddrHash = default!;
        }

        public bool MoveNext()
        {
            // Stackalloc once outside the loop and reuse on every address transition
            // (CA2014 — multiple stackallocs in a loop can blow the stack).
            Span<byte> padded = stackalloc byte[32];
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
                        _suffixEnum = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, prefixEntry.ValueBound);
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
                // by zero-padding the 20-byte column key into a Hash256.
                padded.Clear();
                using (NoOpPin addrPin = Pin(in _reader, addrEntry.KeyBound))
                    addrPin.Buffer.CopyTo(padded);
                _curAddrHash = new Hash256(padded);
                _prefixEnum = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, slotBound);
                _level = 1;
            }
        }

        public readonly StorageEntry Current =>
            new(_reader, _curAddrHash, _curPrefix, _curSuffixKey, _curSuffixValue);

        public void Dispose()
        {
            _suffixEnum.Dispose();
            _prefixEnum.Dispose();
            _addrEnum.Dispose();
        }
    }

    // ---------------- StateNode ----------------

    public readonly ref struct StateNodeEntry(
        PersistedSnapshot snapshot, WholeReadSessionReader reader, Bound key, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly WholeReadSessionReader _reader = reader;
        private readonly Bound _key = key;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path
        {
            get
            {
                using NoOpPin pin = Pin(in _reader, _key);
                ReadOnlySpan<byte> k = pin.Buffer;
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
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _inner;
        private byte _stage; // 0=TopNodes, 1=CompactNodes, 2=Fallback, 3=done
        private Bound _curKey;
        private Bound _curValue;

        public StateNodeEnumerator(PersistedSnapshot snapshot, WholeReadSessionReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _stage = 0;
            _inner = OpenColumn(in _reader, PersistedSnapshot.StateTopNodesTag);
        }

        private static HsstEnumerator<WholeReadSessionReader, NoOpPin> OpenColumn(scoped in WholeReadSessionReader reader, byte[] tag)
        {
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in reader);
            Bound b = r.TrySeek(tag, out _) ? r.GetBound() : default;
            return new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in reader, b);
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

        public readonly StateNodeEntry Current => new(_snapshot, _reader, _curKey, _curValue, _stage);
        public void Dispose() => _inner.Dispose();
    }

    // ---------------- StorageNode ----------------

    public readonly ref struct StorageNodeEntry(
        PersistedSnapshot snapshot, WholeReadSessionReader reader, Hash256 addressHash,
        Bound pathKey, Bound value, byte stage)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly WholeReadSessionReader _reader = reader;
        public Hash256 AddressHash { get; } = addressHash;
        private readonly Bound _pathKey = pathKey;
        private readonly Bound _value = value;
        private readonly byte _stage = stage;
        public TreePath Path
        {
            get
            {
                using NoOpPin pin = Pin(in _reader, _pathKey);
                ReadOnlySpan<byte> k = pin.Buffer;
                return _stage == 0
                    ? PersistedSnapshotReader.DecodeCompactTreePath(k)
                    : new(new ValueHash256(k[..32]), k[32]);
            }
        }
        public ReadOnlySpan<byte> Rlp => _snapshot.ResolveValueAt(_value);
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
        // the inner storage-trie sub-tags in order: compact (0x01) then fallback (0x02).
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _addrEnum;
        private HsstEnumerator<WholeReadSessionReader, NoOpPin> _pathEnum;
        // _stage: 0 = current address-hash's compact sub-tag, 1 = its fallback sub-tag.
        // Reported back to StorageNodeEntry for path-key decoding (compact 8 bytes vs.
        // fallback 33 bytes), so it doubles as the on-disk path-encoding selector.
        private byte _stage;
        private byte _level;  // 0=need new addr, 1=have pathEnum
        private Bound _addrInnerBound;
        private Hash256 _curHash;
        private Bound _curPathKey;
        private Bound _curValue;

        public StorageNodeEnumerator(PersistedSnapshot snapshot, WholeReadSessionReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _stage = 0;
            _level = 0;
            _curHash = default!;
            HsstReader<WholeReadSessionReader, NoOpPin> r = new(in _reader);
            Bound colBound = r.TrySeek(PersistedSnapshot.AccountColumnTag, out _) ? r.GetBound() : default;
            _addrEnum = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in _reader, colBound);
        }

        private static bool TryOpenSubTag(
            scoped in WholeReadSessionReader reader, Bound addrInner, byte[] subTag,
            out HsstEnumerator<WholeReadSessionReader, NoOpPin> e)
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
            e = new HsstEnumerator<WholeReadSessionReader, NoOpPin>(in reader, b);
            return true;
        }

        public bool MoveNext()
        {
            Span<byte> hashKeyPadded = stackalloc byte[32];
            while (true)
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
                    // Try the fallback sub-tag for the same address-hash.
                    if (_stage == 0)
                    {
                        _stage = 1;
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
                if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageCompactSubTag, out _pathEnum))
                {
                    _stage = 1;
                    if (!TryOpenSubTag(in _reader, _addrInnerBound, PersistedSnapshot.StorageFallbackSubTag, out _pathEnum))
                        continue;
                }
                hashKeyPadded.Clear();
                using (NoOpPin pin = Pin(in _reader, addrEntry.KeyBound))
                    pin.Buffer.CopyTo(hashKeyPadded);
                _curHash = new Hash256(hashKeyPadded);
                _level = 1;
            }
        }

        public readonly StorageNodeEntry Current =>
            new(_snapshot, _reader, _curHash, _curPathKey, _curValue, _stage);

        public void Dispose()
        {
            _pathEnum.Dispose();
            _addrEnum.Dispose();
        }
    }
}
