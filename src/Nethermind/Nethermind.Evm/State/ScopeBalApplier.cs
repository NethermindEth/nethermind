// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;

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
        using ArrayPoolList<ValueHash256>? writtenCodeHashes = WriteCode(scope, bal);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(bal.AccountChanges.Count))
        {
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                ApplyAccount(scope, writeBatch, accountChanges);
            }
        }
        MarkCodePersisted(scope, writtenCodeHashes);
    }

    /// <summary>Applies <paramref name="bal"/> as <see cref="Apply"/> does, with the accounts applied concurrently.</summary>
    /// <remarks>
    /// The bytecode is written first, on the calling thread. The scope's reads and its write batch must then support
    /// concurrent use, each thread reading and writing a different account and that account's storage.
    /// </remarks>
    /// <param name="scope">The scope to write into.</param>
    /// <param name="bal">The block access list whose post-block values to write.</param>
    public static void ApplyConcurrently(IWorldStateScopeProvider.IScope scope, ReadOnlyBlockAccessList bal)
    {
        using ArrayPoolList<ValueHash256>? writtenCodeHashes = WriteCode(scope, bal);
        using (IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(bal.AccountChanges.Count))
        {
            ParallelUnbalancedWork.For(0, bal.AccountChanges.Count, (scope, writeBatch, bal),
                static (index, state) =>
                {
                    ApplyAccount(state.scope, state.writeBatch, state.bal.AccountChanges.AsSpan()[index]);
                    return state;
                });
        }
        MarkCodePersisted(scope, writtenCodeHashes);
    }

    /// <summary>Writes the bytecode <paramref name="bal"/> deploys that the scope does not hold yet.</summary>
    /// <returns>The hashes of the written bytecode, or <c>null</c> when none was written.</returns>
    private static ArrayPoolList<ValueHash256>? WriteCode(IWorldStateScopeProvider.IScope scope, ReadOnlyBlockAccessList bal)
    {
        IWorldStateScopeProvider.ICodeSetter? codeSetter = null;
        // Clones deploying the same code in one block would otherwise all write it: ContainsCode only sees persisted code.
        ArrayPoolList<ValueHash256>? writtenCodeHashes = null;
        try
        {
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                if (!accountChanges.HasStateChanges || accountChanges.CodeChanges.Length == 0) continue;

                CodeChange codeChange = accountChanges.CodeChanges[^1];
                if (writtenCodeHashes?.Contains(codeChange.CodeHash) == true || scope.CodeDb.ContainsCode(codeChange.CodeHash)) continue;

                codeSetter ??= scope.CodeDb.BeginCodeWrite();
                codeSetter.Set(codeChange.CodeHash, codeChange.Code);
                (writtenCodeHashes ??= new ArrayPoolList<ValueHash256>(1)).Add(codeChange.CodeHash);
            }
            return writtenCodeHashes;
        }
        catch
        {
            writtenCodeHashes?.Dispose();
            throw;
        }
        finally
        {
            codeSetter?.Dispose();
        }
    }

    private static void MarkCodePersisted(IWorldStateScopeProvider.IScope scope, ArrayPoolList<ValueHash256>? writtenCodeHashes)
    {
        if (writtenCodeHashes is null) return;

        foreach (ValueHash256 codeHash in writtenCodeHashes.AsSpan())
        {
            scope.CodeDb.MarkCodePersisted(in codeHash);
        }
    }

    private static void ApplyAccount(IWorldStateScopeProvider.IScope scope, IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, ReadOnlyAccountChanges accountChanges)
    {
        if (!accountChanges.HasStateChanges) return;

        Address address = accountChanges.Address;
        Account? existing = scope.Get(address);
        if (accountChanges.BalanceChanges.Length == 0 && accountChanges.NonceChanges.Length == 0 && accountChanges.CodeChanges.Length == 0)
        {
            // Slot writes alone are not an account change: they neither create a missing account nor touch an
            // existing one, so EIP-158 leaves an empty account holding storage in place.
            if (existing is not null) WriteSlots(writeBatch, accountChanges);
            return;
        }

        Account account = existing ?? Account.TotallyEmpty;

        if (accountChanges.BalanceChanges.Length > 0) account = account.WithChangedBalance(accountChanges.BalanceChanges[^1].Value);
        if (accountChanges.NonceChanges.Length > 0) account = account.WithChangedNonce(accountChanges.NonceChanges[^1].Value);
        if (accountChanges.CodeChanges.Length > 0) account = account.WithChangedCodeHash(accountChanges.CodeChanges[^1].CodeHash.ToCommitment());

        // EIP-158 is always active with BALs (EIP-7928 postdates Spurious Dragon), so an empty account is removed.
        if (account.IsEmpty)
        {
            writeBatch.Set(address, null);
            return;
        }

        // Storage creation reads the account; keep flat state and the trie aligned until that read completes.
        WriteSlots(writeBatch, accountChanges);
        writeBatch.Set(address, account);
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
}
