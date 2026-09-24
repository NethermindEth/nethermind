// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Evm.State;

/// <summary>
/// Generic <see cref="IWorldStateScopeProvider.IScope.ApplyBal"/> built on the scope's own reads and write batches.
/// </summary>
public static class ScopeBalApplier
{
    /// <inheritdoc cref="IWorldStateScopeProvider.IScope.ApplyBal"/>
    /// <param name="scope">The scope to write into.</param>
    public static void Apply(IWorldStateScopeProvider.IScope scope, ReadOnlyBlockAccessList bal)
    {
        IWorldStateScopeProvider.ICodeSetter? codeSetter = null;
        try
        {
            using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(bal.AccountChanges.Count);
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                if (!accountChanges.HasStateChanges) continue;

                Address address = accountChanges.Address;
                Account account = scope.Get(address) ?? Account.TotallyEmpty;

                if (accountChanges.BalanceChanges.Length > 0) account = account.WithChangedBalance(accountChanges.BalanceChanges[^1].Value);
                if (accountChanges.NonceChanges.Length > 0) account = account.WithChangedNonce(accountChanges.NonceChanges[^1].Value);
                if (accountChanges.CodeChanges.Length > 0)
                {
                    CodeChange codeChange = accountChanges.CodeChanges[^1];
                    if (!scope.CodeDb.ContainsCode(codeChange.CodeHash))
                    {
                        codeSetter ??= scope.CodeDb.BeginCodeWrite();
                        codeSetter.Set(codeChange.CodeHash, codeChange.Code);
                    }
                    account = account.WithChangedCodeHash(codeChange.CodeHash.ToCommitment());
                }

                // EIP-158 is always active with BALs (EIP-7928 postdates Spurious Dragon), so an empty account is removed.
                if (account.IsEmpty)
                {
                    writeBatch.Set(address, null);
                    continue;
                }

                writeBatch.Set(address, account);

                ReadOnlySlotChanges[] storageChanges = accountChanges.StorageChanges;
                if (storageChanges.Length == 0) continue;

                using IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch = writeBatch.CreateStorageWriteBatch(address, storageChanges.Length);
                foreach (ReadOnlySlotChanges slotChanges in storageChanges)
                {
                    if (slotChanges.Changes.Length > 0) storageWriteBatch.Set(slotChanges.Key, slotChanges.Changes[^1].Value);
                }
            }
        }
        finally
        {
            codeSetter?.Dispose();
        }
    }
}
