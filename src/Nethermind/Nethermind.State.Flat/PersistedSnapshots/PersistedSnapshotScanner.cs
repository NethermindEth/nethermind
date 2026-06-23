// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Hsst;
using Nethermind.State.Flat.PersistedSnapshots.Sorted;
using Nethermind.State.Flat.PersistedSnapshots.Storage;
using Nethermind.Trie;

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
/// Streaming scan over a persisted snapshot's single-level <see cref="SortedTable"/>, surfacing the
/// same per-address / state-node / storage-node views the HSST scanner did. Each view does a full
/// forward pass over the table, skipping the columns it does not own (the columns are contiguous in
/// sorted order). Generic over the byte-reader source so the traversal isn't bound to a specific
/// reader; the caller guarantees the underlying region stays valid for the scanner's lifetime.
/// </summary>
public sealed class PersistedSnapshotScanner<TSource, TReader, TPin>(TSource source, PersistedSnapshot snapshot)
    where TSource : IHsstReaderSource<TReader, TPin>
    where TReader : IHsstByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private readonly TSource _source = source;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public PerAddressEnumerable PerAddresses => new(_source.CreateReader());
    public StateNodeEnumerable StateNodes => new(_snapshot, _source.CreateReader());
    public StorageNodeEnumerable StorageNodes => new(_snapshot, _source.CreateReader());

    // ---------------- PerAddress (column 0xFE: Account + SelfDestruct + Slots) ----------------

    public readonly ref struct PerAddressEntry(
        TReader reader, Address address, bool hasAccount, Bound accountBound, bool? selfDestructFlag,
        ReadOnlySpan<byte> slotKeys, ReadOnlySpan<Bound> slotValues)
    {
        private readonly TReader _reader = reader;
        private readonly Bound _accountBound = accountBound;
        private readonly ReadOnlySpan<byte> _slotKeys = slotKeys;
        private readonly ReadOnlySpan<Bound> _slotValues = slotValues;

        public Address Address { get; } = address;
        public bool? SelfDestructFlag { get; } = selfDestructFlag;
        public bool HasAccount { get; } = hasAccount;

        /// <summary>Decoded account, or <c>null</c> when the on-disk marker is <c>[0x00]</c>
        /// (deleted). Branch on <see cref="HasAccount"/> first to tell "no account update in this
        /// snapshot" from "explicitly deleted".</summary>
        public Account? Account
        {
            get
            {
                if (!HasAccount) return null;
                using TPin pin = _reader.PinBuffer(_accountBound);
                ReadOnlySpan<byte> rlp = pin.Buffer;
                if (rlp.Length == 1 && rlp[0] == PersistedSnapshotTags.AccountDeletedMarkerByte) return null;
                return AccountDecoder.Slim.Decode(rlp);
            }
        }

        public SlotEnumerable Slots => new(_reader, _slotKeys, _slotValues);
    }

    public readonly ref struct PerAddressEnumerable(TReader reader)
    {
        private readonly TReader _reader = reader;
        public PerAddressEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct PerAddressEnumerator : IDisposable
    {
        private TReader _reader;
        private SortedTableEnumerator<TReader, TPin> _inner;
        private bool _hasRow;

        private Address? _curAddress;
        private bool _hasAccount;
        private Bound _accountBound;
        private bool? _sdFlag;
        private byte[] _slotKeys;
        private Bound[] _slotValues;
        private int _slotCount;

        public PerAddressEnumerator(TReader reader)
        {
            _reader = reader;
            _inner = new SortedTableEnumerator<TReader, TPin>(in reader, new Bound(0, reader.Length));
            _slotKeys = new byte[PersistedSnapshotKey.SlotLength * 8];
            _slotValues = new Bound[8];
            _hasRow = _inner.MoveNext(in _reader);
        }

        public bool MoveNext()
        {
            // Skip to the next per-address row; stop once we pass it (metadata sorts after).
            while (_hasRow && _inner.CurrentKey[0] != PersistedSnapshotKey.AccountColumn)
            {
                if (_inner.CurrentKey[0] > PersistedSnapshotKey.AccountColumn) { _hasRow = false; break; }
                _hasRow = _inner.MoveNext(in _reader);
            }
            if (!_hasRow) return false;

            _curAddress = new Address(PersistedSnapshotKey.PerAddressAddress(_inner.CurrentKey));
            _hasAccount = false;
            _accountBound = default;
            _sdFlag = null;
            _slotCount = 0;

            while (_hasRow && _inner.CurrentKey[0] == PersistedSnapshotKey.AccountColumn &&
                   PersistedSnapshotKey.PerAddressAddress(_inner.CurrentKey).SequenceEqual(_curAddress.Bytes))
            {
                byte sub = PersistedSnapshotKey.PerAddressSubColumn(_inner.CurrentKey);
                if (sub == PersistedSnapshotKey.SlotSub)
                {
                    BufferSlot(PersistedSnapshotKey.SlotKeyBytes(_inner.CurrentKey), _inner.CurrentValue);
                }
                else if (sub == PersistedSnapshotKey.SelfDestructSub)
                {
                    byte flag = 0;
                    _reader.TryRead(_inner.CurrentValue.Offset, new Span<byte>(ref flag));
                    _sdFlag = flag != PersistedSnapshotTags.SelfDestructDestructedMarkerByte;
                }
                else // account
                {
                    _hasAccount = true;
                    _accountBound = _inner.CurrentValue;
                }
                _hasRow = _inner.MoveNext(in _reader);
            }
            return true;
        }

        private void BufferSlot(ReadOnlySpan<byte> slot32, Bound valueBound)
        {
            if (_slotCount == _slotValues.Length)
            {
                Array.Resize(ref _slotValues, _slotValues.Length * 2);
                byte[] grown = new byte[_slotKeys.Length * 2];
                _slotKeys.CopyTo(grown.AsSpan());
                _slotKeys = grown;
            }
            slot32.CopyTo(_slotKeys.AsSpan(_slotCount * PersistedSnapshotKey.SlotLength));
            _slotValues[_slotCount] = valueBound;
            _slotCount++;
        }

        public readonly PerAddressEntry Current => new(
            _reader, _curAddress!, _hasAccount, _accountBound, _sdFlag,
            _slotKeys.AsSpan(0, _slotCount * PersistedSnapshotKey.SlotLength), _slotValues.AsSpan(0, _slotCount));

        public void Dispose() { }
    }

    // ---------------- Slot (nested inside PerAddressEntry) ----------------

    public readonly ref struct SlotEntry(TReader reader, ReadOnlySpan<byte> slot32, Bound value)
    {
        private readonly TReader _reader = reader;
        private readonly ReadOnlySpan<byte> _slot = slot32;
        private readonly Bound _value = value;

        public UInt256 Slot => new(_slot, isBigEndian: true);

        public SlotValue? Value
        {
            get
            {
                if (_value.Length == 0) return null;
                using TPin pin = _reader.PinBuffer(_value);
                ReadOnlySpan<byte> value = new Rlp.ValueDecoderContext(pin.Buffer).DecodeByteArraySpan();
                return SlotValue.FromSpanWithoutLeadingZero(value);
            }
        }
    }

    public readonly ref struct SlotEnumerable(TReader reader, ReadOnlySpan<byte> slotKeys, ReadOnlySpan<Bound> slotValues)
    {
        private readonly TReader _reader = reader;
        private readonly ReadOnlySpan<byte> _slotKeys = slotKeys;
        private readonly ReadOnlySpan<Bound> _slotValues = slotValues;
        public SlotEnumerator GetEnumerator() => new(_reader, _slotKeys, _slotValues);
    }

    public ref struct SlotEnumerator(TReader reader, ReadOnlySpan<byte> slotKeys, ReadOnlySpan<Bound> slotValues)
    {
        private readonly TReader _reader = reader;
        private readonly ReadOnlySpan<byte> _slotKeys = slotKeys;
        private readonly ReadOnlySpan<Bound> _slotValues = slotValues;
        private int _index = -1;

        public bool MoveNext() => ++_index < _slotValues.Length;

        public readonly SlotEntry Current => new(
            _reader,
            _slotKeys.Slice(_index * PersistedSnapshotKey.SlotLength, PersistedSnapshotKey.SlotLength),
            _slotValues[_index]);
    }

    // ---------------- StateNode (columns 0xFB/0xFC/0xFD) ----------------

    public readonly ref struct StateNodeEntry(PersistedSnapshot snapshot, ReadOnlySpan<byte> key, Bound value)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _key = key;
        private readonly Bound _value = value;

        public TreePath Path => PersistedSnapshotKey.DecodePath(
            PersistedSnapshotKey.StatePathBytes(_key), StateStage(_key[0]));

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
        private TReader _reader;
        private SortedTableEnumerator<TReader, TPin> _inner;
        private bool _hasRow;
        private bool _returnedRow;

        public StateNodeEnumerator(PersistedSnapshot snapshot, TReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _inner = new SortedTableEnumerator<TReader, TPin>(in reader, new Bound(0, reader.Length));
            _hasRow = _inner.MoveNext(in _reader);
        }

        public bool MoveNext()
        {
            if (_returnedRow)
            {
                _hasRow = _inner.MoveNext(in _reader);
                _returnedRow = false;
            }
            while (_hasRow)
            {
                byte col = _inner.CurrentKey[0];
                if (col is PersistedSnapshotKey.StateTopColumn or PersistedSnapshotKey.StateCompactColumn or PersistedSnapshotKey.StateFallbackColumn)
                {
                    _returnedRow = true;
                    return true;
                }
                // State columns (FB/FC/FD) sit between storage (FA) and per-address (FE); once
                // past them there is nothing more to yield.
                if (col > PersistedSnapshotKey.StateTopColumn) { _hasRow = false; break; }
                _hasRow = _inner.MoveNext(in _reader);
            }
            return false;
        }

        public readonly StateNodeEntry Current => new(_snapshot, _inner.CurrentKey, _inner.CurrentValue);

        public void Dispose() { }
    }

    // ---------------- StorageNode (column 0xFA) ----------------

    public readonly ref struct StorageNodeEntry(PersistedSnapshot snapshot, ValueHash256 addressHash, ReadOnlySpan<byte> key, Bound value)
    {
        private readonly PersistedSnapshot _snapshot = snapshot;
        private readonly ReadOnlySpan<byte> _key = key;
        private readonly Bound _value = value;

        public ValueHash256 AddressHash { get; } = addressHash;

        public TreePath Path => PersistedSnapshotKey.DecodePath(
            PersistedSnapshotKey.StoragePathBytes(_key), StorageStage(PersistedSnapshotKey.StorageSubColumn(_key)));

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
        private TReader _reader;
        private SortedTableEnumerator<TReader, TPin> _inner;
        private bool _hasRow;
        private bool _returnedRow;

        public StorageNodeEnumerator(PersistedSnapshot snapshot, TReader reader)
        {
            _snapshot = snapshot;
            _reader = reader;
            _inner = new SortedTableEnumerator<TReader, TPin>(in reader, new Bound(0, reader.Length));
            _hasRow = _inner.MoveNext(in _reader);
        }

        public bool MoveNext()
        {
            if (_returnedRow)
            {
                _hasRow = _inner.MoveNext(in _reader);
                _returnedRow = false;
            }
            while (_hasRow)
            {
                byte col = _inner.CurrentKey[0];
                if (col == PersistedSnapshotKey.StorageColumn) { _returnedRow = true; return true; }
                // Storage (FA) is the first column; once past it there is nothing more to yield.
                if (col > PersistedSnapshotKey.StorageColumn) { _hasRow = false; break; }
                _hasRow = _inner.MoveNext(in _reader);
            }
            return false;
        }

        public readonly StorageNodeEntry Current
        {
            get
            {
                ValueHash256 hash = default;
                PersistedSnapshotKey.StorageAddressHash(_inner.CurrentKey).CopyTo(hash.BytesAsSpan);
                return new StorageNodeEntry(_snapshot, hash, _inner.CurrentKey, _inner.CurrentValue);
            }
        }

        public void Dispose() { }
    }

    private static int StateStage(byte column) => column switch
    {
        PersistedSnapshotKey.StateTopColumn => 0,
        PersistedSnapshotKey.StateCompactColumn => 1,
        _ => 2,
    };

    private static int StorageStage(byte subColumn) => subColumn switch
    {
        PersistedSnapshotKey.StorageTopSub => 0,
        PersistedSnapshotKey.StorageCompactSub => 1,
        _ => 2,
    };
}
