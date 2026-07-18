// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Io;
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
    public static WholeReadScanner ForWholeRead(WholeReadSession session, PersistedSnapshot snapshot) =>
        new(session, snapshot);
}

/// <summary>
/// The <see cref="PersistedSnapshotScanner{TSource,TReader,TPin}"/> instantiation over a
/// <see cref="WholeReadSession"/>, named so consumers don't need a fully-qualified generic alias.
/// </summary>
public sealed class WholeReadScanner(WholeReadSession session, PersistedSnapshot snapshot)
    : PersistedSnapshotScanner<WholeReadSession, WholeReadSessionReader, NoOpPin>(session, snapshot);

/// <summary>
/// Streaming scan over a persisted snapshot's single-level <see cref="SortedTable"/>, surfacing the
/// same per-address / state-node / storage-node views the prior columnar scanner did. Each view does a full
/// forward pass over the table, skipping the columns it does not own (the columns are contiguous in
/// sorted order). Generic over the byte-reader source so the traversal isn't bound to a specific
/// reader; the caller guarantees the underlying region stays valid for the scanner's lifetime.
/// </summary>
public class PersistedSnapshotScanner<TSource, TReader, TPin>(TSource source, PersistedSnapshot snapshot)
    where TSource : IByteReaderSource<TReader, TPin>
    where TReader : IByteReader<TPin>, allows ref struct
    where TPin : struct, IBufferPin, allows ref struct
{
    private readonly TSource _source = source;
    private readonly PersistedSnapshot _snapshot = snapshot;

    public PerAddressEnumerable PerAddresses => new(_source.CreateReader());
    public StateNodeEnumerable StateNodes => new(_snapshot, _source.CreateReader());
    public StorageNodeEnumerable StorageNodes => new(_snapshot, _source.CreateReader());

    // ---------------- PerAddress (column 0xFE: Account + SelfDestruct + Slots) ----------------

    public readonly ref struct PerAddressEntry
    {
        // The per-address pass's own cursor, parked on this address's first slot. The Slots view streams it
        // directly, so it advances as the caller drains Slots.
        private readonly ref SortedTableEnumerator<TReader, TPin> _inner;
        private readonly TReader _reader;
        private readonly Bound _accountBound;
        private readonly bool _hasSlots;

        public Address Address { get; }
        public bool? SelfDestructFlag { get; }
        public bool HasAccount { get; }

        // internal (not the public primary-ctor form) because it takes the internal SortedTableEnumerator by
        // ref; callers only ever receive a PerAddressEntry from the enumerator, never construct one.
        internal PerAddressEntry(
            ref SortedTableEnumerator<TReader, TPin> inner, TReader reader, Address address, bool hasAccount,
            Bound accountBound, bool? selfDestructFlag, bool hasSlots)
        {
            _inner = ref inner;
            _reader = reader;
            _accountBound = accountBound;
            _hasSlots = hasSlots;
            Address = address;
            SelfDestructFlag = selfDestructFlag;
            HasAccount = hasAccount;
        }

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

        /// <summary>Streams the address's slots straight off the shared per-address cursor rather than
        /// buffering them — an account can carry an unbounded number of storage slots. Addresses with no
        /// slots cost nothing.</summary>
        /// <remarks>A single forward pass over the shared cursor: iterate it at most once, and before the
        /// enclosing per-address enumerator advances. Any slots left undrained are skipped by the outer
        /// enumerator's next <c>MoveNext</c>.</remarks>
        public SlotEnumerable Slots => new(ref _inner, _reader, Address, _hasSlots);
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
        private bool _hasSlots;

        public PerAddressEnumerator(TReader reader)
        {
            _reader = reader;
            _inner = new SortedTableEnumerator<TReader, TPin>(in reader, new Bound(0, reader.Length));
            _hasRow = _inner.MoveNext(in _reader);
        }

        public bool MoveNext()
        {
            // The Slots view of the previous entry streams this same cursor; skip any slots it left undrained
            // so the cursor is on the next group before we look for it.
            SkipToNextGroup();

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

            // A group's sub-columns sort Account (0xFD) < SelfDestruct (0xFE) < Slots (0xFF). Read the account
            // and self-destruct eagerly; leave the cursor parked on the first slot so the Slots view can
            // stream it off this same cursor.
            if (IsCurrentSub(PersistedSnapshotKey.AccountSub))
            {
                _hasAccount = true;
                _accountBound = _inner.CurrentValue;
                _hasRow = _inner.MoveNext(in _reader);
            }

            if (IsCurrentSub(PersistedSnapshotKey.SelfDestructSub))
            {
                byte flag = 0;
                _reader.TryRead(_inner.CurrentValue.Offset, new Span<byte>(ref flag));
                _sdFlag = flag != PersistedSnapshotTags.SelfDestructDestructedMarkerByte;
                _hasRow = _inner.MoveNext(in _reader);
            }

            _hasSlots = IsCurrentSub(PersistedSnapshotKey.SlotSub);
            return true;
        }

        // True when the cursor is still on the current address's per-address group at sub-column <paramref name="sub"/>.
        private readonly bool IsCurrentSub(byte sub) =>
            _hasRow &&
            _inner.CurrentKey[0] == PersistedSnapshotKey.AccountColumn &&
            PersistedSnapshotKey.PerAddressSubColumn(_inner.CurrentKey) == sub &&
            PersistedSnapshotKey.PerAddressAddress(_inner.CurrentKey).SequenceEqual(_curAddress!.Bytes);

        // Advance the cursor past the current address's remaining slot rows — those the Slots view did not
        // drain (or all of them, if the caller ignored Slots). Slots are the group's last sub-column, so this
        // lands on the next group's first row (or the first row past the per-address column). A no-op in the
        // common case where Slots was fully drained; otherwise a forward walk over the undrained slots (the
        // caller ignoring a large account's Slots is a rare, defensive case).
        private void SkipToNextGroup()
        {
            if (_curAddress is null) return;
            while (IsCurrentSub(PersistedSnapshotKey.SlotSub))
                _hasRow = _inner.MoveNext(in _reader);
        }

        [UnscopedRef]
        public PerAddressEntry Current =>
            new(ref _inner, _reader, _curAddress!, _hasAccount, _accountBound, _sdFlag, _hasSlots);

        public void Dispose() => _inner.Dispose();
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
                ReadOnlySpan<byte> value = new RlpReader(pin.Buffer).DecodeByteArraySpan();
                return SlotValue.FromSpanWithoutLeadingZero(value);
            }
        }
    }

    public readonly ref struct SlotEnumerable
    {
        private readonly ref SortedTableEnumerator<TReader, TPin> _inner;
        private readonly TReader _reader;
        private readonly Address _address;
        private readonly bool _hasSlots;

        internal SlotEnumerable(ref SortedTableEnumerator<TReader, TPin> inner, TReader reader, Address address, bool hasSlots)
        {
            _inner = ref inner;
            _reader = reader;
            _address = address;
            _hasSlots = hasSlots;
        }

        public SlotEnumerator GetEnumerator() => new(ref _inner, _reader, _address, _hasSlots);
    }

    public ref struct SlotEnumerator
    {
        // The shared per-address cursor, parked on the address's first slot. Not owned here — the per-address
        // enumerator disposes it — so streaming just advances it and leaves it for the outer to skip on.
        private readonly ref SortedTableEnumerator<TReader, TPin> _inner;
        private TReader _reader;
        private readonly Address _address;
        private bool _active;
        // The cursor is already parked on the first slot, so the first MoveNext yields it; later calls advance.
        private bool _started;

        internal SlotEnumerator(ref SortedTableEnumerator<TReader, TPin> inner, TReader reader, Address address, bool hasSlots)
        {
            _inner = ref inner;
            _reader = reader;
            _address = address;
            _active = hasSlots;
        }

        public bool MoveNext()
        {
            if (!_active) return false;
            if (_started)
            {
                if (!_inner.MoveNext(in _reader)) { _active = false; return false; }
            }
            else
            {
                _started = true;
            }
            if (IsSlotOf(_inner.CurrentKey)) return true; // account/SD sort first, so only a different address stops it
            _active = false;
            return false;
        }

        // The next address's rows (different address) end the run; the address check is load-bearing because a
        // following address's slots share this row's column and sub-tag.
        private readonly bool IsSlotOf(ReadOnlySpan<byte> key) =>
            key[0] == PersistedSnapshotKey.AccountColumn
            && PersistedSnapshotKey.PerAddressSubColumn(key) == PersistedSnapshotKey.SlotSub
            && PersistedSnapshotKey.PerAddressAddress(key).SequenceEqual(_address.Bytes);

        public readonly SlotEntry Current =>
            new(_reader, PersistedSnapshotKey.SlotKeyBytes(_inner.CurrentKey), _inner.CurrentValue);
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

        public void Dispose() => _inner.Dispose();
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

        public void Dispose() => _inner.Dispose();
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
