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
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Applies the suggested-block BAL deltas onto the shared world state at the end of parallel
/// execution, and finalises the block by stamping the generated BAL + its encoded RLP + its
/// hash onto the produced block.
/// </summary>
public partial class BlockAccessListManager
{
    /// <summary>
    /// Applies the suggested-block BAL deltas onto <paramref name="stateProvider"/> so the post-block
    /// world state matches the wire BAL.
    /// </summary>
    /// <remarks>
    /// For every account in the suggested BAL, the last balance/nonce/code change is replayed
    /// against <paramref name="stateProvider"/>, and each declared slot's last storage change
    /// is written. Storage <em>reads</em> (slots that appear in <c>StorageReads</c> only) are
    /// not applied — they describe what the block <em>observed</em>, not what it changed.
    /// </remarks>
    public static void ApplyStateChanges(ReadOnlyBlockAccessList suggestedBlockAccessList, IWorldState stateProvider, IReleaseSpec spec, bool shouldComputeStateRoot)
    {
        foreach (ReadOnlyAccountChanges accountChanges in suggestedBlockAccessList.AccountChanges)
        {
            if (accountChanges.BalanceChanges.Length > 0)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                UInt256 oldBalance = stateProvider.GetBalance(accountChanges.Address);
                UInt256 newBalance = accountChanges.BalanceChanges[^1].Value;
                if (newBalance > oldBalance)
                {
                    stateProvider.AddToBalance(accountChanges.Address, newBalance - oldBalance, spec);
                }
                else if (newBalance < oldBalance)
                {
                    stateProvider.SubtractFromBalance(accountChanges.Address, oldBalance - newBalance, spec);
                }
            }

            if (accountChanges.NonceChanges.Length > 0)
            {
                stateProvider.CreateAccountIfNotExists(accountChanges.Address, 0, 0);
                stateProvider.SetNonce(accountChanges.Address, accountChanges.NonceChanges[^1].Value);
            }

            if (accountChanges.CodeChanges.Length > 0)
            {
                stateProvider.InsertCode(accountChanges.Address, accountChanges.CodeChanges[^1].Code, spec);
            }

            foreach (ReadOnlySlotChanges slotChange in accountChanges.StorageChanges)
            {
                StorageCell storageCell = new(accountChanges.Address, slotChange.Key);
                int slotCount = slotChange.Changes.Length;
                if (slotCount > 0)
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
            return;
        }

        CheckInitialized();
        MergeAndReturnBal(uint.MaxValue);

        if (!ParallelExecutionEnabled && _validateBlockAccessList && block.BlockAccessList is not null)
        {
            uint lastIndex = (uint)(block.Transactions.Length + 1);
            for (uint index = 0; index <= lastIndex; index++)
            {
                ValidateBlockAccessList(block, index, validateStorageReads: index == lastIndex);
            }
            ValidateStructuralEquivalence(block);
        }

        if (VerifyOnly)
        {
            // IncrementalValidation only covered indices 0..txCount; the post-execution row
            // (txCount + 1) was just merged but not yet compared.
            ValidateBlockAccessList(block, (uint)(block.Transactions.Length + 1));
            ValidateStructuralEquivalence(block);
            return;
        }

        block.GeneratedBlockAccessList = GeneratedBlockAccessList;
        block.EncodedBlockAccessList = BlockAccessListDecoder.EncodeToBytes(GeneratedBlockAccessList);
        block.Header.BlockAccessListHash = new(ValueKeccak.Compute(block.EncodedBlockAccessList).Bytes);
    }
}
