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

    private readonly List<(Address Address, Account? Account)> _accountWrites = [];
    private readonly List<(Address Address, UInt256 Index, byte[] Value)> _storageWrites = [];
    private readonly List<(ValueHash256 CodeHash, byte[] Code)> _codeWrites = [];

    private readonly HashSet<AddressAsKey> _writtenAccounts = [];
    private readonly HashSet<StorageCell> _writtenStorageCells = [];

    // Coinbase accumulator
    private Account? _coinbaseBefore;
    private UInt256 _coinbaseBalanceDelta;

    public void RecordAccountRead(Address address, Account? account)
    {
        if (CoinbaseAddress is not null && address == CoinbaseAddress)
        {
            _coinbaseBefore ??= account;
            return;
        }

        _readAccounts.Add(address);
    }

    public void RecordStorageRead(in StorageCell cell) =>
        _readStorageCells.Add(cell);

    public void RecordAccountWrite(Address address, Account? account)
    {
        if (CoinbaseAddress is not null && address == CoinbaseAddress)
        {
            if (account is not null && _coinbaseBefore is not null && account.Balance >= _coinbaseBefore.Balance)
                _coinbaseBalanceDelta = account.Balance - _coinbaseBefore.Balance;
            else if (account is not null && _coinbaseBefore is null)
                _coinbaseBalanceDelta = account.Balance;
            return;
        }

        _accountWrites.Add((address, account));
        _writtenAccounts.Add(address);
    }

    public void RecordStorageWrite(Address address, in UInt256 index, byte[] value)
    {
        StorageCell cell = new(address, index);
        _storageWrites.Add((address, index, value));
        _writtenStorageCells.Add(cell);
    }

    public void RecordCodeWrite(in ValueHash256 codeHash, byte[] code) =>
        _codeWrites.Add((codeHash, code));

    /// <summary>
    /// Returns the current diff and resets all collections for reuse.
    /// </summary>
    public TransactionStateDiff TakeDiff()
    {
        TransactionStateDiff diff = new() { CoinbaseBalanceDelta = _coinbaseBalanceDelta };

        foreach (AddressAsKey addr in _readAccounts) diff.ReadAccounts.Add(addr);
        foreach (StorageCell cell in _readStorageCells) diff.ReadStorageCells.Add(cell);
        foreach ((Address addr, Account? acct) in _accountWrites) diff.AccountWrites.Add((addr, acct));
        foreach ((Address addr, UInt256 idx, byte[] val) in _storageWrites) diff.StorageWrites.Add((addr, idx, val));
        foreach ((ValueHash256 hash, byte[] code) in _codeWrites) diff.CodeWrites.Add((hash, code));
        foreach (AddressAsKey addr in _writtenAccounts) diff.WrittenAccounts.Add(addr);
        foreach (StorageCell cell in _writtenStorageCells) diff.WrittenStorageCells.Add(cell);

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
        _writtenAccounts.Clear();
        _writtenStorageCells.Clear();
        _coinbaseBefore = null;
        _coinbaseBalanceDelta = UInt256.Zero;
    }
}
