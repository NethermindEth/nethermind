// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

internal enum ScanOutcome : byte
{
    Fits,
    SinglePathOverflow,
    Split,
}

internal sealed class StorageRootMoveCheck(StoragePresenceProbe probe, ulong to, List<HistoryWalkMismatch> mismatches)
{
    public void OnMoved(in ValueHash256 accountPath, ulong block, in ValueHash256 previous, in ValueHash256 current)
    {
        if (probe.HasStorageHistory(accountPath, to)) return;

        mismatches.Add(new HistoryWalkMismatch(block, HistoryWalkMismatchKind.MissingSlotHistory, previous, current));
    }
}

internal sealed class HistoryRowScanner(
    ISortedKeyValueStore accountHistory,
    ISortedKeyValueStore storageHistory,
    ISortedKeyValueStore storageClears,
    HistoryRowFormat rowFormat)
{
    public const int AccountRowKeyLength = Hash256.Size + sizeof(ulong);
    public const int StorageRowKeyLength = BaseFlatPersistence.StorageKeyLength + sizeof(ulong);
    public const int ClearRowKeyLength = Hash256.Size + sizeof(ulong);
    public const int IdentityLength = BaseFlatPersistence.AccountKeyLength;
    public const int StoragePrefixLength = BasePersistence.StoragePrefixPortion;
    public const int SlotOffset = StoragePrefixLength;
    public const int IdentitySuffixOffset = SlotOffset + Hash256.Size;
    public const int IdentitySuffixLength = IdentityLength - StoragePrefixLength;

    public HistoryRowFormat RowFormat => rowFormat;

    public ScanOutcome ScanAccounts(in TreePath prefix, ulong from, ulong to, long maxRows, AccountPartitionRows rows, StorageRootMoveCheck check, CancellationToken token)
    {
        Span<byte> lower = stackalloc byte[AccountRowKeyLength];
        Span<byte> upper = stackalloc byte[AccountRowKeyLength + 1];
        WriteAccountBounds(prefix, lower, upper);

        using ISortedView view = accountHistory.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss);
        ValueHash256 currentPath = default;
        bool havePath = false;
        bool skipping = false;
        bool startTaken = false;
        bool havePending = false;
        ulong pendingBlock = 0;
        ValueHash256 pendingRoot = default;
        int distinctPaths = 0;

        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != AccountRowKeyLength) continue;

            ReadOnlySpan<byte> pathBytes = key[..Hash256.Size];
            if (!havePath || !pathBytes.SequenceEqual(currentPath.Bytes))
            {
                if (havePending && pendingRoot != Keccak.EmptyTreeHash.ValueHash256) check.OnMoved(currentPath, pendingBlock, Keccak.EmptyTreeHash.ValueHash256, pendingRoot);

                currentPath = new ValueHash256(pathBytes);
                havePath = true;
                startTaken = false;
                havePending = false;
                skipping = rows.StreamedPaths.Contains(currentPath);
                if (!skipping) distinctPaths++;
            }

            if (skipping) continue;

            ulong block = rowFormat.DecodeSuffixBlock(key[Hash256.Size..]);
            if (block > to) continue;

            ReadOnlySpan<byte> value = view.CurrentValue;
            ValueHash256 root = value.IsEmpty ? Keccak.EmptyTreeHash.ValueHash256 : ContractRootCheck.StorageRootOf(value);
            if (havePending)
            {
                if (root != pendingRoot) check.OnMoved(currentPath, pendingBlock, root, pendingRoot);
                havePending = false;
            }

            if (block > from)
            {
                if (rows.Count >= maxRows) return Overflow(rows, currentPath, distinctPaths);

                rows.Deltas.Add(new AccountRowRef(currentPath, block, rows.Arena.Append(value), value.Length));
                havePending = true;
                pendingBlock = block;
                pendingRoot = root;
                continue;
            }

            if (startTaken) continue;

            startTaken = true;
            if (value.IsEmpty) continue;
            if (rows.Count >= maxRows) return Overflow(rows, currentPath, distinctPaths);

            rows.Start.Add(new AccountRowRef(currentPath, from, rows.Arena.Append(value), value.Length));
        }

        if (havePending && pendingRoot != Keccak.EmptyTreeHash.ValueHash256) check.OnMoved(currentPath, pendingBlock, Keccak.EmptyTreeHash.ValueHash256, pendingRoot);
        return ScanOutcome.Fits;
    }

    public ScanOutcome ScanStorage(ReadOnlySpan<byte> storagePrefix, in TreePath slotPrefix, ulong from, ulong to, long maxRows, StoragePartitionRows rows, IReadOnlyList<ClearRecord> clears, CancellationToken token)
    {
        Span<byte> lower = stackalloc byte[StorageRowKeyLength];
        Span<byte> upper = stackalloc byte[StorageRowKeyLength + 1];
        WriteStorageBounds(storagePrefix, slotPrefix, lower, upper);

        StorageRowCollector collector = new(rows, clears, from, to, maxRows, rowFormat);
        using ISortedView view = storageHistory.GetViewBetween(lower, upper, ReadFlags.HintCacheMiss);
        while (view.MoveNext())
        {
            token.ThrowIfCancellationRequested();
            if (view.CurrentKey.Length != StorageRowKeyLength) continue;

            if (!collector.TryAdd(view.CurrentKey, view.CurrentValue))
            {
                if (collector.DistinctKeys == 1)
                {
                    rows.StreamedSlots.Add(collector.Current);
                    rows.Reset();
                    return ScanOutcome.SinglePathOverflow;
                }

                rows.Reset();
                return ScanOutcome.Split;
            }
        }

        return ScanOutcome.Fits;
    }

    public List<ClearRecord> LoadClears(ReadOnlySpan<byte> storagePrefix, ulong to)
    {
        List<ClearRecord> clears = [];
        Span<byte> lower = stackalloc byte[ClearRowKeyLength];
        lower.Clear();
        storagePrefix.CopyTo(lower);
        Span<byte> upper = stackalloc byte[ClearRowKeyLength + 1];
        upper.Fill(0xFF);
        storagePrefix.CopyTo(upper);
        upper[^1] = 0x00;

        using ISortedView view = storageClears.GetViewBetween(lower, upper);
        while (view.MoveNext())
        {
            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != ClearRowKeyLength) continue;

            ulong block = BinaryPrimitives.ReadUInt64BigEndian(key[Hash256.Size..]);
            if (block > to) continue;

            ValueHash256 identity = default;
            key[..IdentityLength].CopyTo(identity.BytesAsSpan);
            clears.Add(new ClearRecord(identity, block));
        }

        return clears;
    }

    public static bool KilledByClear(IReadOnlyList<ClearRecord> clears, in ValueHash256 identity, ulong writtenAt, ulong asOf)
    {
        for (int i = 0; i < clears.Count; i++)
        {
            ClearRecord clear = clears[i];
            if (clear.Identity == identity && clear.Block > writtenAt && clear.Block <= asOf) return true;
        }

        return false;
    }

    public static void WriteAccountBounds(in TreePath prefix, Span<byte> lower, Span<byte> upper)
    {
        lower.Clear();
        upper.Fill(0xFF);
        WritePathBounds(prefix, lower, upper);
        upper[^1] = 0x00;
    }

    public static void WriteStorageBounds(ReadOnlySpan<byte> storagePrefix, in TreePath slotPrefix, Span<byte> lower, Span<byte> upper)
    {
        lower.Clear();
        upper.Fill(0xFF);
        storagePrefix.CopyTo(lower);
        storagePrefix.CopyTo(upper);
        WritePathBounds(slotPrefix, lower[SlotOffset..], upper[SlotOffset..]);
        upper[^1] = 0x00;
    }

    public static void WriteStorageFlatKey(Span<byte> destination, in ValueHash256 identity, in ValueHash256 slot)
    {
        identity.Bytes[..StoragePrefixLength].CopyTo(destination);
        slot.Bytes.CopyTo(destination[SlotOffset..]);
        identity.Bytes.Slice(StoragePrefixLength, IdentitySuffixLength).CopyTo(destination[IdentitySuffixOffset..]);
    }

    public static ValueHash256 IdentityOf(ReadOnlySpan<byte> storageRowKey)
    {
        ValueHash256 identity = default;
        Span<byte> bytes = identity.BytesAsSpan;
        storageRowKey[..StoragePrefixLength].CopyTo(bytes);
        storageRowKey.Slice(IdentitySuffixOffset, IdentitySuffixLength).CopyTo(bytes[StoragePrefixLength..]);
        return identity;
    }

    private static void WritePathBounds(in TreePath prefix, Span<byte> lower, Span<byte> upper)
    {
        int wholeBytes = prefix.Length / 2;
        prefix.Path.Bytes[..wholeBytes].CopyTo(lower);
        prefix.Path.Bytes[..wholeBytes].CopyTo(upper);
        if ((prefix.Length & 1) == 1)
        {
            byte half = (byte)(prefix.Path.Bytes[wholeBytes] & 0xF0);
            lower[wholeBytes] = half;
            upper[wholeBytes] = (byte)(half | 0x0F);
        }
    }

    private static ScanOutcome Overflow(AccountPartitionRows rows, in ValueHash256 currentPath, int distinctPaths)
    {
        if (distinctPaths == 1)
        {
            rows.StreamedPaths.Add(currentPath);
            rows.Reset();
            return ScanOutcome.SinglePathOverflow;
        }

        rows.Reset();
        return ScanOutcome.Split;
    }
}

internal sealed class StorageRowCollector(StoragePartitionRows rows, IReadOnlyList<ClearRecord> clears, ulong from, ulong to, long maxRows, HistoryRowFormat rowFormat)
{
    private readonly byte[] _currentFlatKey = new byte[BaseFlatPersistence.StorageKeyLength];
    private bool _haveKey;
    private bool _skipping;
    private bool _startTaken;
    private int _contract;
    private ValueHash256 _identity;
    private ValueHash256 _slot;

    public int DistinctKeys { get; private set; }

    public (int Contract, ValueHash256 Slot) Current => (_contract, _slot);

    public bool TryAdd(ReadOnlySpan<byte> key, ReadOnlySpan<byte> value)
    {
        ReadOnlySpan<byte> flatKey = key[..BaseFlatPersistence.StorageKeyLength];
        if (!_haveKey || !flatKey.SequenceEqual(_currentFlatKey))
        {
            flatKey.CopyTo(_currentFlatKey);
            _haveKey = true;
            _startTaken = false;
            _identity = HistoryRowScanner.IdentityOf(key);
            _slot = new ValueHash256(key.Slice(HistoryRowScanner.SlotOffset, Hash256.Size));
            _contract = rows.ContractOf(_identity);
            _skipping = rows.StreamedSlots.Contains((_contract, _slot));
            if (!_skipping) DistinctKeys++;
        }

        if (_skipping) return true;

        ulong block = rowFormat.DecodeSuffixBlock(key[BaseFlatPersistence.StorageKeyLength..]);
        if (block > to) return true;

        if (block > from)
        {
            if (rows.Count >= maxRows) return false;

            rows.Deltas.Add(new StorageRowRef(_contract, _slot, block, rows.Arena.Append(value), value.Length));
            return true;
        }

        if (_startTaken) return true;

        _startTaken = true;
        if (value.IsEmpty) return true;
        if (HistoryRowScanner.KilledByClear(clears, _identity, writtenAt: block, asOf: from)) return true;
        if (rows.Count >= maxRows) return false;

        rows.Start.Add(new StorageRowRef(_contract, _slot, from, rows.Arena.Append(value), value.Length));
        return true;
    }
}
