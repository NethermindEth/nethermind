// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.Persistence;
using Nethermind.Trie;

namespace Nethermind.State.Flat.History.Walk;

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
        long scanned = 0;
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
            if ((++scanned & (WalkProgress.RowsPerUpdate - 1)) == 0) token.ThrowIfCancellationRequested();

            ReadOnlySpan<byte> key = view.CurrentKey;
            if (key.Length != AccountRowKeyLength) continue;

            ReadOnlySpan<byte> pathBytes = key[..Hash256.Size];
            if (!havePath || !pathBytes.SequenceEqual(currentPath.Bytes))
            {
                if (havePending) FinishPath(check, currentPath, pendingBlock, pendingRoot);

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
            ValueHash256 root = StorageRootOf(value);
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

        if (havePending) FinishPath(check, currentPath, pendingBlock, pendingRoot);
        return ScanOutcome.Fits;
    }

    public void ScanStorageGroups(byte firstByte, ulong from, ulong to, long maxRows, uint? afterPrefix, Action<StorageGroup> onGroup, Action<uint> onPosition, CancellationToken token)
    {
        long scanned = 0;
        byte[] lower = new byte[StorageRowKeyLength];
        lower[0] = firstByte;
        if (afterPrefix is { } done)
        {
            if (done == uint.MaxValue) return;

            BinaryPrimitives.WriteUInt32BigEndian(lower, done + 1);
        }

        byte[] end = new byte[StorageRowKeyLength + 1];
        if (firstByte == byte.MaxValue) end.AsSpan().Fill(0xFF);
        else end[0] = (byte)(firstByte + 1);

        while (true)
        {
            byte[]? nextLower = null;
            StorageGroup? group = null;
            StorageRowCollector? collector = null;
            bool overflow = false;

            try
            {
                using ISortedView view = storageHistory.GetViewBetween(lower, end, ReadFlags.HintCacheMiss);
                while (view.MoveNext())
                {
                    if ((++scanned & (WalkProgress.RowsPerUpdate - 1)) == 0) token.ThrowIfCancellationRequested();

                    ReadOnlySpan<byte> key = view.CurrentKey;
                    if (key.Length != StorageRowKeyLength) continue;

                    ReadOnlySpan<byte> prefix = key[..StoragePrefixLength];
                    if (group is null)
                    {
                        onPosition((uint)((prefix[1] << 16) | (prefix[2] << 8) | prefix[3]));
                        List<ClearRecord> clears = ScanClears(prefix, to);
                        StoragePartitionRows rows = new();
                        collector = new StorageRowCollector(rows, clears, from, to, maxRows, rowFormat);
                        group = new StorageGroup(prefix.ToArray(), rows, clears, Overflow: false);
                    }
                    else if (!prefix.SequenceEqual(group.Prefix))
                    {
                        nextLower = new byte[StorageRowKeyLength];
                        prefix.CopyTo(nextLower);
                        break;
                    }

                    if (overflow) continue;
                    if (!collector!.TryAdd(key, view.CurrentValue)) overflow = true;
                }
            }
            catch
            {
                group?.Rows.Dispose();
                throw;
            }

            if (group is null) return;

            onGroup(overflow ? group with { Overflow = true } : group);
            if (nextLower is null) return;

            lower = nextLower;
        }
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

    public List<ClearRecord> ScanClears(ReadOnlySpan<byte> storagePrefix, ulong to)
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

    public static ValueHash256 StorageRootOf(ReadOnlySpan<byte> accountRow)
    {
        if (accountRow.IsEmpty) return Keccak.EmptyTreeHash.ValueHash256;

        RlpReader reader = new(accountRow);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref reader, out AccountStruct account))
        {
            throw new InvalidOperationException("An account history row failed to decode; the column is corrupt.");
        }

        return account.StorageRoot;
    }

    public static Account? DecodeAccount(ReadOnlySpan<byte> accountRow)
    {
        if (accountRow.IsEmpty) return null;

        RlpReader reader = new(accountRow);
        if (!AccountDecoder.Slim.TryDecodeStruct(ref reader, out AccountStruct account))
        {
            throw new InvalidOperationException("An account history row failed to decode; the column is corrupt.");
        }

        return new Account(account.Nonce, account.Balance, account.StorageRoot.ToCommitment(), account.CodeHash.ToCommitment());
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

    private static void FinishPath(StorageRootMoveCheck check, in ValueHash256 path, ulong block, in ValueHash256 root)
    {
        if (root != Keccak.EmptyTreeHash.ValueHash256) check.OnMoved(path, block, Keccak.EmptyTreeHash.ValueHash256, root);
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
