// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
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
    /// <param name="logManager">Reports adopted code whose bytecode cannot be found, and disposal failures swallowed after a failed apply.</param>
    public static void Apply(IWorldStateScopeProvider.IScope scope, ReadOnlyBlockAccessList bal, ILogManager logManager)
    {
        ILogger logger = logManager.GetClassLogger(typeof(ScopeBalApplier));
        IWorldStateScopeProvider.ICodeSetter? codeSetter = null;
        // Clones deploying the same code in one block would otherwise all write it: ContainsCode only sees persisted code.
        ArrayPoolList<ValueHash256>? writtenCodeHashes = null;
        bool failed = false;
        try
        {
            try
            {
                IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch = scope.StartWriteBatch(bal.AccountChanges.Count);
                try
                {
                    // Disposed at the end of this block, so unclaimed storage batches are disposed before the parent.
                    using StorageBatches storageBatches = new(logger);
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
                                ReportMissingCode(logger, address, codeChange.CodeHash);
                            }
                        }

                        // Storage creation reads the account; keep flat state and the trie aligned until that read completes.
                        storageBatches.Add(writeBatch, delta, existing is not null);
                        // A slot-only change leaves the account as it was.
                        if (delta.ChangesAccount) writeBatch.Set(address, account);
                    }
                    storageBatches.Apply();
                }
                catch
                {
                    failed = true;
                    throw;
                }
                finally
                {
                    Dispose(writeBatch, failed, logger);
                }
            }
            catch
            {
                // Also covers the parent batch's own disposal failing, which the code setter's disposal must not replace.
                failed = true;
                throw;
            }
            finally
            {
                Dispose(codeSetter, failed, logger);
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

    /// <remarks>
    /// After a failure, a second fault while disposing must not replace the original exception, so it is logged and
    /// swallowed. Disposal commits writes, so on success its failure propagates.
    /// </remarks>
    private static void Dispose(IDisposable? disposable, bool failed, in ILogger logger)
    {
        if (!failed)
        {
            disposable?.Dispose();
            return;
        }

        try { disposable?.Dispose(); }
        catch (Exception e)
        {
            if (logger.IsWarn) logger.Warn($"Swallowed disposal failure of {disposable!.GetType().Name} after a failed BAL apply: {e}");
        }
    }

    /// <remarks>
    /// EIP-8298: the account still takes the adopted hash, as execution would; reads of the code then rely on the
    /// code database being healed.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReportMissingCode(in ILogger logger, Address address, in ValueHash256 codeHash)
    {
        Metrics.IncrementBalAdoptedCodeMissing();
        if (logger.IsWarn) logger.Warn($"Block access list adopts code {codeHash} for {address}, but its bytecode is neither in the code database nor deposited in the block.");
    }

    private sealed class StorageBatches(ILogger logger) : IDisposable
    {
        private const int MinParallelBatches = 3;
        private bool _applied;
        private readonly ArrayPoolList<(IWorldStateScopeProvider.IStorageWriteBatch? Batch, BalAccountDelta Delta)> _batches = new(0);

        public void Add(IWorldStateScopeProvider.IWorldStateWriteBatch writeBatch, in BalAccountDelta delta, bool existed)
        {
            bool clear = delta.WipesStorage && existed;
            int count = delta.Changes.StorageChanges.Length;
            if (count == 0 && !clear) return;

            // Scope-owned storage-tree lookup stays serial; only independent batches are handed to workers.
            IWorldStateScopeProvider.IStorageWriteBatch batch = writeBatch.CreateStorageWriteBatch(delta.Changes.Address, count);
            _batches.Add((batch, delta));
            // Clearing can touch scope-wide caches, so it stays serial too; it must precede any slot write.
            if (clear) batch.Clear();
        }

        public void Apply()
        {
            if (_batches.Count < MinParallelBatches)
            {
                for (int i = 0; i < _batches.Count; i++) Apply(i);
            }
            else
            {
                _batches.AsSpan().Sort(static (a, b) => b.Delta.Changes.StorageChanges.Length.CompareTo(a.Delta.Changes.StorageChanges.Length));
                ParallelUnbalancedWork.For(0, _batches.Count, this, static (i, state) =>
                {
                    state.Apply(i);
                    return state;
                });
            }
            _applied = true;
        }

        private void Apply(int index)
        {
            ref (IWorldStateScopeProvider.IStorageWriteBatch? Batch, BalAccountDelta Delta) work = ref _batches.GetRef(index);
            IWorldStateScopeProvider.IStorageWriteBatch batch = work.Batch!;
            foreach (ReadOnlySlotChanges changes in work.Delta.Changes.StorageChanges)
            {
                if (work.Delta.TryGetFinalValue(changes, out UInt256 value)) batch.Set(changes.Key, value);
            }
            // Claimed only once its writes succeed: a failed batch is left to Dispose, so its own disposal cannot mask the failure.
            work.Batch = null;
            batch.Dispose();
        }

        /// <remarks>
        /// A completed <see cref="Apply()"/> claims and disposes every batch, so batches remain only after a failure and
        /// their disposal failures are swallowed. Should one remain after a completed apply, its failure propagates.
        /// </remarks>
        public void Dispose()
        {
            try
            {
                // The parallel loop joins before returning or throwing; only unclaimed batches remain owned here.
                foreach ((IWorldStateScopeProvider.IStorageWriteBatch? batch, _) in _batches.AsSpan())
                {
                    if (batch is not null) ScopeBalApplier.Dispose(batch, failed: !_applied, logger);
                }
            }
            finally
            {
                _batches.Dispose();
            }
        }
    }
}
