// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.State.Flat.Persistence;

internal static class AccountBatchReaderExtensions
{
    internal static void ReadAccounts(this IPersistence.IPersistenceReader reader, ReadOnlySpan<Address> addresses,
        Span<Account?> accounts)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(accounts.Length, addresses.Length, nameof(accounts));
        if (addresses.Length >= AccountReadBatch.MinReadSize && reader is IAccountBatchReader batch)
        {
            batch.GetAccounts(addresses, accounts);
            return;
        }
        for (int i = 0; i < addresses.Length; i++) accounts[i] = reader.GetAccount(addresses[i]);
    }
}
