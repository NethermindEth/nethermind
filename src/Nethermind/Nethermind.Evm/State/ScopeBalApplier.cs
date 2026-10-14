// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm.State;

/// <summary>
/// Generic <see cref="IWorldStateScopeProvider.IScope.ApplyBal"/> built on the scope's own reads and write batches.
/// </summary>
/// <remarks>What the list does to each account is computed by <see cref="BalAccountDelta"/>.</remarks>
public static class ScopeBalApplier
{
    /// <inheritdoc cref="IWorldStateScopeProvider.IScope.ApplyBal"/>
    /// <param name="scope">The scope to write into.</param>
    /// <param name="logManager">Reports adopted code whose bytecode cannot be found.</param>
    public static void Apply(IWorldStateScopeProvider.IScope scope, ReadOnlyBlockAccessList bal, ILogManager logManager)
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
                    BalAccountDelta delta = BalAccountDelta.Compute(existing, accountChanges);

                    if (delta.Account is not { } account)
                    {
                        if (existing is not null) writeBatch.Set(address, null);
                        continue;
                    }

                    if (delta.Code is { } codeChange
                        && writtenCodeHashes?.Contains(codeChange.CodeHash) != true && !scope.CodeDb.ContainsCode(codeChange.CodeHash))
                    {
                        if (BalAccountDelta.TryGetCode(bal, codeChange, out byte[]? code))
                        {
                            codeSetter ??= scope.CodeDb.BeginCodeWrite();
                            codeSetter.Set(codeChange.CodeHash, code);
                            (writtenCodeHashes ??= new ArrayPoolList<ValueHash256>(1)).Add(codeChange.CodeHash);
                        }
                        else
                        {
                            ReportMissingCode(logManager, address, codeChange.CodeHash);
                        }
                    }

                    // Storage creation reads the account; keep flat state and the trie aligned until that read completes.
                    WriteSlots(writeBatch, delta, existing is not null);
                    // A slot-only change leaves the account as it was.
                    if (delta.ChangesAccount) writeBatch.Set(address, account);
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

    private static void WriteSlots(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, in BalAccountDelta delta, bool existed)
    {
        bool clear = delta.WipesStorage && existed;
        ReadOnlySlotChanges[] storageChanges = delta.Changes.StorageChanges;
        if (storageChanges.Length == 0 && !clear) return;

        using IWorldStateScopeProvider.IStorageWriteBatch storageWriteBatch = writeBatch.CreateStorageWriteBatch(delta.Changes.Address, storageChanges.Length);
        if (clear) storageWriteBatch.Clear();
        foreach (ReadOnlySlotChanges slotChanges in storageChanges)
        {
            if (delta.TryGetFinalValue(slotChanges, out UInt256 value)) storageWriteBatch.Set(slotChanges.Key, value);
        }
    }

    /// <remarks>
    /// EIP-8298: the account still takes the adopted hash, as execution would; reads of the code then rely on the
    /// code database being healed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportMissingCode(ILogManager logManager, Address address, in ValueHash256 codeHash)
    {
        Metrics.IncrementBalAdoptedCodeMissing();
        ILogger logger = logManager.GetClassLogger(typeof(ScopeBalApplier));
        if (logger.IsWarn) logger.Warn($"Block access list adopts code {codeHash} for {address}, but its bytecode is neither in the code database nor deposited in the block.");
    }
}
