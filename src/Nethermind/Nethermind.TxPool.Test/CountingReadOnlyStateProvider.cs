// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Evm.State;

namespace Nethermind.TxPool.Test;

/// <summary>Counts per-address account reads, so a test can assert which reads a pool actually makes.</summary>
/// <remarks><see cref="TxPool"/> caches accounts in front of this, so a count is only meaningful for an address
/// evicted from that cache first — see <see cref="TxPool.ResetAddress"/>.</remarks>
internal sealed class CountingReadOnlyStateProvider(IReadOnlyStateProvider inner) : IReadOnlyStateProvider
{
    private readonly ConcurrentDictionary<AddressAsKey, int> _reads = new();

    public int AccountReads(Address address) => _reads.TryGetValue(address, out int count) ? count : 0;

    public void ResetCounts() => _reads.Clear();

    public bool TryGetAccount(Address address, out AccountStruct account)
    {
        _reads.AddOrUpdate(address, 1, static (_, count) => count + 1);
        return inner.TryGetAccount(address, out account);
    }

    public Hash256 StateRoot => inner.StateRoot;

    public byte[]? GetCode(Address address) => inner.GetCode(address);

    public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);

    public bool IsContract(Address address) => inner.IsContract(address);

    public bool AccountExists(Address address) => inner.AccountExists(address);

    public bool IsDeadAccount(Address address) => inner.IsDeadAccount(address);

    public ReadOnlySpan<byte> Get(in StorageCell storageCell) => inner.Get(in storageCell);
}
