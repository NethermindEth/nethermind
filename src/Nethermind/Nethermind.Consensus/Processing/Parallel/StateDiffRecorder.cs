// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing.Parallel;

/// <summary>
/// Collects state reads and writes fed by <see cref="StateDiffScopeProviderDecorator"/>.
/// One per worker, reused across transactions via <see cref="TakeDiff"/>.
/// </summary>
public class StateDiffRecorder
{
    public Address? CoinbaseAddress { get; set; }

    private readonly HashSet<AddressAsKey> _readAccounts = [];
    private readonly HashSet<StorageCell> _readStorageCells = [];
    private readonly Dictionary<AddressAsKey, (Account? Before, Account? After)> _accountWrites = [];
    private readonly Dictionary<StorageCell, byte[]> _storageWrites = [];
    private readonly List<(Address Address, ValueHash256 CodeHash, byte[] Code)> _codeWrites = [];

    // Track first-seen account values to know the "before" when a write comes later
    private readonly Dictionary<AddressAsKey, Account?> _firstReadAccounts = [];

    // Coinbase accumulator
    private Account? _coinbaseBefore;
    private UInt256 _coinbaseBalanceDelta;

    public void RecordAccountRead(Address address, Account? account)
    {
        AddressAsKey key = address;

        if (CoinbaseAddress is not null && address == CoinbaseAddress)
        {
            _coinbaseBefore ??= account;
            return;
        }

        _readAccounts.Add(key);
        _firstReadAccounts.TryAdd(key, account);
    }

    public void RecordStorageRead(in StorageCell cell)
    {
        _readStorageCells.Add(cell);
    }

    public void RecordAccountWrite(Address address, Account? after)
    {
        AddressAsKey key = address;

        if (CoinbaseAddress is not null && address == CoinbaseAddress)
        {
            if (after is not null && _coinbaseBefore is not null && after.Balance >= _coinbaseBefore.Balance)
            {
                _coinbaseBalanceDelta = after.Balance - _coinbaseBefore.Balance;
            }
            else if (after is not null && _coinbaseBefore is null)
            {
                _coinbaseBalanceDelta = after.Balance;
            }

            return;
        }

        Account? before = _firstReadAccounts.GetValueOrDefault(key);
        _accountWrites[key] = (before, after);
    }

    public void RecordStorageWrite(Address address, in UInt256 index, byte[] value)
    {
        StorageCell cell = new(address, index);
        _storageWrites[cell] = value;
    }

    public void RecordCodeWrite(Address address, in ValueHash256 codeHash, byte[] code)
    {
        _codeWrites.Add((address, codeHash, code));
    }

    /// <summary>
    /// Returns the current diff and resets all collections for reuse.
    /// </summary>
    public TransactionStateDiff TakeDiff()
    {
        TransactionStateDiff diff = new()
        {
            CoinbaseBalanceDelta = _coinbaseBalanceDelta
        };

        foreach (AddressAsKey addr in _readAccounts) diff.ReadAccounts.Add(addr);
        foreach (StorageCell cell in _readStorageCells) diff.ReadStorageCells.Add(cell);
        foreach (KeyValuePair<AddressAsKey, (Account? Before, Account? After)> kv in _accountWrites) diff.AccountWrites[kv.Key] = kv.Value;
        foreach (KeyValuePair<StorageCell, byte[]> kv in _storageWrites) diff.StorageWrites[kv.Key] = kv.Value;
        foreach ((Address Address, ValueHash256 CodeHash, byte[] Code) entry in _codeWrites) diff.CodeWrites.Add(entry);

        Reset();
        return diff;
    }

    public void Reset()
    {
        _readAccounts.Clear();
        _readStorageCells.Clear();
        _accountWrites.Clear();
        _storageWrites.Clear();
        _codeWrites.Clear();
        _firstReadAccounts.Clear();
        _coinbaseBefore = null;
        _coinbaseBalanceDelta = UInt256.Zero;
    }
}
