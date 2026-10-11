// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;

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
        // Clones deploying the same code in one block would otherwise all write it: ContainsCode only sees persisted code.
        ArrayPoolList<ValueHash256>? writtenCodeHashes = null;
        try
        {
            try
            {
                using IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(bal.AccountChanges.Count);
                foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
                {
                    if (!accountChanges.HasStateChanges) continue;

                    Address address = accountChanges.Address;
                    Account? existing = scope.Get(address);
                    if (accountChanges.BalanceChanges.Length == 0 && accountChanges.NonceChanges.Length == 0 && accountChanges.CodeChanges.Length == 0)
                    {
                        // Slot writes alone are not an account change: they neither create a missing account nor touch an
                        // existing one, so EIP-158 leaves an empty account holding storage in place.
                        if (existing is not null) WriteSlots(writeBatch, accountChanges);
                        continue;
                    }

                    Account account = existing ?? Account.TotallyEmpty;

                    if (accountChanges.BalanceChanges.Length > 0) account = account.WithChangedBalance(accountChanges.BalanceChanges[^1].Value);
                    if (accountChanges.NonceChanges.Length > 0) account = account.WithChangedNonce(accountChanges.NonceChanges[^1].Value);
                    if (accountChanges.CodeChanges.Length > 0)
                    {
                        CodeChange codeChange = accountChanges.CodeChanges[^1];
                        if (writtenCodeHashes?.Contains(codeChange.CodeHash) != true && !scope.CodeDb.ContainsCode(codeChange.CodeHash)
                            && TryGetCode(bal, codeChange, out byte[]? code))
                        {
                            codeSetter ??= scope.CodeDb.BeginCodeWrite();
                            codeSetter.Set(codeChange.CodeHash, code);
                            (writtenCodeHashes ??= new ArrayPoolList<ValueHash256>(1)).Add(codeChange.CodeHash);
                        }
                        account = account.WithChangedCodeHash(codeChange.CodeHash.ToCommitment());
                    }

                    // EIP-158 is always active with BALs (EIP-7928 postdates Spurious Dragon), so an empty account is removed.
                    if (account.IsEmpty)
                    {
                        writeBatch.Set(address, null);
                        continue;
                    }

                    // Storage creation reads the account; keep flat state and the trie aligned until that read completes.
                    WriteSlots(writeBatch, accountChanges);
                    writeBatch.Set(address, account);
                }
            }
            finally
            {
                codeSetter?.Dispose();
            }

            if (writtenCodeHashes is null) return;

            foreach (ValueHash256 codeHash in writtenCodeHashes.AsSpan())
            {
                scope.CodeDb.MarkCodePersisted(in codeHash);
            }
        }
        finally
        {
            writtenCodeHashes?.Dispose();
        }
    }

    private static void WriteSlots(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, ReadOnlyAccountChanges accountChanges)
    {
        ReadOnlySlotChanges[] storageChanges = accountChanges.StorageChanges;
        if (storageChanges.Length == 0) return;

        using IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch = writeBatch.CreateStorageWriteBatch(accountChanges.Address, storageChanges.Length);
        foreach (ReadOnlySlotChanges slotChanges in storageChanges)
        {
            if (slotChanges.Changes.Length > 0) storageWriteBatch.Set(slotChanges.Key, slotChanges.Changes[^1].Value);
        }
    }

    /// <remarks>
    /// EIP-8298: an adopted change carries only a hash. Bytecode the code database lacks was deposited earlier in the
    /// block, possibly by an account whose final code differs, so it is taken from that change.
    /// </remarks>
    private static bool TryGetCode(ReadOnlyBlockAccessList bal, in CodeChange codeChange, [NotNullWhen(true)] out byte[]? code)
    {
        if (!codeChange.IsAdopted)
        {
            code = codeChange.Code;
            return true;
        }

        if (bal.GetCodeChangesByHash()?.TryGetValue(codeChange.CodeHash, out (uint Index, byte[] Code) declared) == true)
        {
            code = declared.Code;
            return true;
        }

        code = null;
        return false;
    }
}
