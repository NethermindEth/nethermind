// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.History.Walk;

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
