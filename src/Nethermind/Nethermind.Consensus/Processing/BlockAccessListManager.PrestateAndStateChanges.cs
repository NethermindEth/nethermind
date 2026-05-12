// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Prestate loading (populating the suggested BAL with start-of-block values from the inner
/// state) and state-apply (writing the suggested BAL's deltas onto <c>stateProvider</c> so the
/// post-block world state matches the wire BAL). Also finalises the block by stamping the
/// generated BAL + its encoded RLP + its hash onto the produced block.
/// </summary>
public partial class BlockAccessListManager
{
    public void LoadPreStateToSuggestedBlockAccessList(Block suggestedBlock)
    {
        if (!ParallelExecutionEnabled || suggestedBlock.BlockAccessList is null) return;

        // Skip if this exact BAL was already loaded — see _lastLoadedBal field comment. The
        // workers' gates were also skipped in PrepareForProcessing in that case, so nothing
        // to signal here either.
        if (suggestedBlock.Hash == _lastLoadedBal) return;

        // Wire BAL validation must run before this method: it appends local-only
        // PrestateIndex entries that are sorted before real tx indices and must not be
        // subjected to block-level wire index-bounds validation.
        ReadOnlyBlockAccessList bal = suggestedBlock.BlockAccessList;
        try
        {
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                // record whether the account was modified before any prestate is added
                accountChanges.RecordWasChanged();

                bool exists = stateProvider.TryGetAccount(accountChanges.Address, out AccountStruct account);
                accountChanges.SetExistedBeforeBlock(exists);
                accountChanges.SetEmptyBeforeBlock(!account.HasStorage);

                accountChanges.LoadPreStateBalance(account.Balance);
                accountChanges.LoadPreStateNonce((ulong)account.Nonce);
                accountChanges.LoadPreStateCode(stateProvider.GetCode(accountChanges.Address) ?? []);

                // snapshot keys to avoid modifying the slot collection during iteration
                // (LoadPreStateStorage can insert a new ReadOnlySlotChanges for a previously read-only slot)
                UInt256[] slotsToLoad = [.. accountChanges.GetSlotsForPreStateLoad()];
                foreach (UInt256 slot in slotsToLoad)
                {
                    StorageCell storageCell = new(accountChanges.Address, slot);
                    UInt256 value = new(stateProvider.Get(storageCell), true);
                    accountChanges.LoadPreStateStorage(slot, value);
                }

                // Signal as soon as this account is fully loaded — workers blocked on it via
                // ReadOnlyAccountChanges.WaitForPrestate can proceed without waiting for the
                // remaining accounts to finish.
                accountChanges.SignalPrestateLoaded();
            }
        }
        catch
        {
            // If the loader throws partway, release every remaining gate so workers and the
            // incremental validator don't hang. Already-signaled gates are no-ops; not-yet-
            // loaded accounts will return their pre-load (empty / default) state, which will
            // surface as an InvalidBlockLevelAccessListException on the worker — preferable
            // to a deadlock. The original exception still propagates from slot 0.
            foreach (ReadOnlyAccountChanges accountChanges in bal.AccountChanges)
            {
                accountChanges.SignalPrestateLoaded();
            }
            throw;
        }

        // Record success only after a complete load — a retry against the same block hash
        // (e.g. after a transient inner-state read fault) must re-enter this method and
        // mutate the BAL in-place. Setting this before the try would silently skip the
        // retry's load via the early-return above.
        _lastLoadedBal = suggestedBlock.Hash;
    }

    /// <summary>
    /// Applies the suggested-block BAL deltas onto <paramref name="stateProvider"/> so the post-block
    /// world state matches the wire BAL.
    /// </summary>
    /// <remarks>
    /// Requires <paramref name="suggestedBlockAccessList"/> to have already been prestate-loaded
    /// via <see cref="LoadPreStateToSuggestedBlockAccessList"/>. The <c>oldBalance</c> calculation
    /// below relies on a prestate balance entry being present (it falls back to zero only for
    /// brand-new accounts created in this block). Callers in the parallel and sequential paths
    /// run prestate load as the first step; do not invoke this on a freshly-decoded BAL.
    /// </remarks>
    public static void ApplyStateChanges(ReadOnlyBlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
    {
        foreach (ReadOnlyAccountChanges accountChanges in suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.BalanceChanges.Length > 0 && accountChanges.BalanceChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                // GetBalance(0) returns the prestate entry (the only change with index < 0 in the
                // ordering used by PrestateAwareIndexComparer). Null only for accounts created
                // mid-block — those have no prestate balance, so 0 is the correct delta base.
                UInt256 oldBalance = accountChanges.GetBalance(0) ?? UInt256.Zero;
                UInt256 newBalance = accountChanges.BalanceChanges[^1].Value;
                if (newBalance > oldBalance)
                {
                    stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                }
                else
                {
                    stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                }
            }

            if (accountChanges.NonceChanges.Length > 0 && accountChanges.NonceChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[^1].Value);
            }

            if (accountChanges.CodeChanges.Length > 0 && accountChanges.CodeChanges[^1].Index != Eip7928Constants.PrestateIndex)
            {
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[^1].Code, spec);
            }

            foreach (ReadOnlySlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChange.Key);
                // could be empty since prestate loaded
                int slotCount = slotChange.Changes.Length;
                if (slotCount > 0 && slotChange.Changes[^1].Index != Eip7928Constants.PrestateIndex)
                {
                    // StorageChange.Value is now EvmWord (Vector256<byte>) in big-endian wire form.
                    EvmWord value = slotChange.Changes[^1].Value;
                    ReadOnlySpan<byte> valueBytes = MemoryMarshal.CreateReadOnlySpan(
                        ref Unsafe.As<EvmWord, byte>(ref value), 32);
                    stateProvider.Set(storageCell, [.. valueBytes.WithoutLeadingZeros()]);
                }
            }
        }
        stateProvider.Commit(spec);
        if (shouldComputeStateRoot)
        {
            stateProvider.RecalculateStateRoot();
        }
    }

    public void SetBlockAccessList(Block block)
    {
        if (!_blockAccessListsEnabled)
        {
            return;
        }

        if (block.IsGenesis)
        {
            block.Header.BlockAccessListHash = Keccak.OfAnEmptySequenceRlp;
        }
        else
        {
            CheckInitialized();

            _txProcessorWithWorldStateManager.MergeAndReturnBal(uint.MaxValue, GeneratedBlockAccessList);
            block.GeneratedBlockAccessList = GeneratedBlockAccessList;
            block.EncodedBlockAccessList = Rlp.Encode(GeneratedBlockAccessList).Bytes;
            block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
        }
    }
}
