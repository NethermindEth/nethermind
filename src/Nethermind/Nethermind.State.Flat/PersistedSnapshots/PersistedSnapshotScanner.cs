// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public static PersistedSnapshotScanner<WholeReadSession, WholeReadSessionReader, NoOpPin> ForWholeRead(
        WholeReadSession session, PersistedSnapshot snapshot) =>
        new(session, snapshot);
}

/// <summary>
/// Streaming scan over a persisted snapshot's single-level <see cref="SortedTable"/>, surfacing the
/// same per-address / state-node / storage-node views the prior columnar scanner did. Each view does a full
/// forward pass over the table, skipping the columns it does not own (the columns are contiguous in
/// sorted order). Generic over the byte-reader source so the traversal isn't bound to a specific
/// reader; the caller guarantees the underlying region stays valid for the scanner's lifetime.
/// </summary>
public sealed class PersistedSnapshotScanner<TSource, TReader, TPin>(TSource source, PersistedSnapshot snapshot)
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

    public readonly ref struct PerAddressEntry(
        TReader reader, Address address, bool hasAccount, Bound accountBound, bool? selfDestructFlag,
        bool hasSlots, SortedTableCursor slotCursor)
    {
        private readonly TReader _reader = reader;
        private readonly Bound _accountBound = accountBound;
        private readonly bool _hasSlots = hasSlots;
        private readonly SortedTableCursor _slotCursor = slotCursor;

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

        /// <summary>Streams the address's slots straight off the table rather than buffering them — an
        /// account can carry an unbounded number of storage slots. Resumes from the block the per-address
        /// pass already walked to (no re-seek); addresses with no slots cost nothing.</summary>
        public SlotEnumerable Slots => new(_reader, Address, _hasSlots, _slotCursor);
    }

    public readonly ref struct PerAddressEnumerable(TReader reader)
    {
        private readonly TReader _reader = reader;
        public PerAddressEnumerator GetEnumerator() => new(_reader);
    }

    public ref struct PerAddressEnumerator : IDisposable
    {
        // Slots to scan past linearly before binary-searching to the account; a small account (the common
        // case) reaches its account within the budget, avoiding a re-seek.
        private const int MaxLinearSlotSkip = 32;

        private TReader _reader;
        private SortedTableEnumerator<TReader, TPin> _inner;
        private bool _hasRow;

        private Address? _curAddress;
        private bool _hasAccount;
        private Bound _accountBound;
        private bool? _sdFlag;
        private bool _hasSlots;
        private SortedTableCursor _slotCursor;

        public PerAddressEnumerator(TReader reader)
        {
            _reader = reader;
            _inner = new SortedTableEnumerator<TReader, TPin>(in reader, new Bound(0, reader.Length));
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
            _hasSlots = false;

            // A group's sub-columns sort SelfDestruct (0xFD) < Slots (0xFE) < Account (0xFF).
            if (IsCurrentSub(PersistedSnapshotKey.SelfDestructSub))
            {
                byte flag = 0;
                _reader.TryRead(_inner.CurrentValue.Offset, new Span<byte>(ref flag));
                _sdFlag = flag != PersistedSnapshotTags.SelfDestructDestructedMarkerByte;
                _hasRow = _inner.MoveNext(in _reader);
            }

            // Slots come next but are streamed lazily by the Slots view — do not read or buffer them here.
            // Jump past them to the account (which sorts last in the group).
            if (IsCurrentSub(PersistedSnapshotKey.SlotSub))
            {
                _hasSlots = true;
                _slotCursor = _inner.Capture(); // record the first slot's block before skipping to the account
                SkipSlotsToAccount();
            }

            if (IsCurrentSub(PersistedSnapshotKey.AccountSub))
            {
                _hasAccount = true;
                _accountBound = _inner.CurrentValue;
                _hasRow = _inner.MoveNext(in _reader);
            }
            return true;
        }

        // True when the cursor is still on the current address's per-address group at sub-column <paramref name="sub"/>.
        private readonly bool IsCurrentSub(byte sub) =>
            _hasRow &&
            _inner.CurrentKey[0] == PersistedSnapshotKey.AccountColumn &&
            PersistedSnapshotKey.PerAddressSubColumn(_inner.CurrentKey) == sub &&
            PersistedSnapshotKey.PerAddressAddress(_inner.CurrentKey).SequenceEqual(_curAddress!.Bytes);

        // On entry the cursor is on the address's first slot; on exit it is on the account (if any) or the
        // first row past the group. Scans a bounded number of slots linearly, then binary-searches to the
        // account key so an account with a huge number of slots costs O(1) here.
        private void SkipSlotsToAccount()
        {
            for (int i = 0; i < MaxLinearSlotSkip; i++)
            {
                _hasRow = _inner.MoveNext(in _reader);
                if (!IsCurrentSub(PersistedSnapshotKey.SlotSub)) return;
            }

            Span<byte> accountKey = stackalloc byte[PersistedSnapshotKey.MaxKeyLength];
            int len = PersistedSnapshotKey.WriteAccountKey(accountKey, _curAddress!.Bytes);
            if (!_inner.Seek(in _reader, accountKey[..len]))
            {
                _hasRow = false;
                return;
            }
            // Seek lands on the block holding the ceiling; skip within it to the first row ≥ the account key
            // (the account itself, or the next group when this address has no account record).
            _hasRow = _inner.MoveNext(in _reader);
            while (_hasRow && _inner.CurrentKey.SequenceCompareTo(accountKey[..len]) < 0)
                _hasRow = _inner.MoveNext(in _reader);
        }

        public readonly PerAddressEntry Current =>
            new(_reader, _curAddress!, _hasAccount, _accountBound, _sdFlag, _hasSlots, _slotCursor);

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

    public readonly ref struct SlotEnumerable(
        TReader reader, Address address, bool hasSlots, SortedTableCursor slotCursor)
    {
        private readonly TReader _reader = reader;
        private readonly Address _address = address;
        private readonly bool _hasSlots = hasSlots;
        private readonly SortedTableCursor _slotCursor = slotCursor;
        public SlotEnumerator GetEnumerator() => new(_reader, _address, _hasSlots, _slotCursor);
    }

    public ref struct SlotEnumerator(TReader reader, Address address, bool hasSlots, SortedTableCursor slotCursor) : IDisposable
    {
        private TReader _reader = reader;
        // Resume from the block the per-address pass already walked to for the first slot — no re-seek.
        // Addresses with no slots stay inert (no enumerator).
        private SortedTableEnumerator<TReader, TPin> _inner =
            hasSlots ? new SortedTableEnumerator<TReader, TPin>(in reader, in slotCursor) : default;
        private readonly Address _address = address;
        private readonly bool _active = hasSlots;

        public bool MoveNext()
        {
            if (!_active) return false;
            while (_inner.MoveNext(in _reader))
            {
                int rel = SlotRelation(_inner.CurrentKey);
                if (rel < 0) continue;     // row precedes this address's slots (block fill before the seek target)
                if (rel > 0) return false; // past this address's slots
                return true;
            }
            return false;
        }

        // Orders a row against this address's slot range: &lt;0 before, 0 a slot of the address, &gt;0 past.
        private readonly int SlotRelation(ReadOnlySpan<byte> key)
        {
            if (key[0] != PersistedSnapshotKey.AccountColumn)
                return key[0] < PersistedSnapshotKey.AccountColumn ? -1 : 1;
            int addrCmp = PersistedSnapshotKey.PerAddressAddress(key).SequenceCompareTo(_address.Bytes);
            if (addrCmp != 0) return addrCmp;
            byte sub = PersistedSnapshotKey.PerAddressSubColumn(key);
            return sub == PersistedSnapshotKey.SlotSub ? 0 : sub < PersistedSnapshotKey.SlotSub ? -1 : 1;
        }

        public readonly SlotEntry Current =>
            new(_reader, PersistedSnapshotKey.SlotKeyBytes(_inner.CurrentKey), _inner.CurrentValue);

        public void Dispose()
        {
            if (_active) _inner.Dispose();
        }
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
