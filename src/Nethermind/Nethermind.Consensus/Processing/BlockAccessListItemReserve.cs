// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Consensus.Processing;

/// <summary>
/// Upper bound on the EIP-7928 items the end-of-block steps can add, reserved during transaction selection so the
/// block stays within <c>block_gas_limit / ITEM_COST</c>.
/// </summary>
/// <remarks>
/// Withdrawals and the request-queue system calls (EIP-7002, EIP-7251, EIP-8282) spend no block gas. Withdrawals add
/// one item per new recipient. Each queue adds its address and four header words, plus the slots of records queued
/// before the block, up to its per-block maximum.
/// </remarks>
public static class BlockAccessListItemReserve
{
    /// <summary>Address item plus the excess, count, head and tail words of a request queue contract.</summary>
    internal const int QueueFixedItems = 1 + 4;

    private static readonly UInt256 QueueHeadSlot = 2;
    private static readonly UInt256 QueueTailSlot = 3;

    internal const int MaxWithdrawalRequestsPerBlock = 16;
    internal const int WithdrawalRequestSlots = 3;
    internal const int MaxConsolidationRequestsPerBlock = 2;
    internal const int ConsolidationRequestSlots = 4;
    internal const int MaxBuilderDepositRequestsPerBlock = 64;
    internal const int BuilderDepositRequestSlots = 6;
    internal const int MaxBuilderExitRequestsPerBlock = 16;
    internal const int BuilderExitRequestSlots = 3;

    /// <summary>Items the end-of-block steps of <paramref name="block"/> can add to <paramref name="blockAccessList"/>.</summary>
    /// <param name="block">The block being built; only its withdrawals are read.</param>
    /// <param name="spec">The block's release spec.</param>
    /// <param name="state">State at the start of transaction selection; read without recording accesses.</param>
    /// <param name="blockAccessList">The list as built so far, used to skip items already present.</param>
    public static long Calculate(Block block, IReleaseSpec spec, IWorldState state, BlockAccessListItemSet blockAccessList)
    {
        long reserve = spec.WithdrawalsEnabled ? WithdrawalRecipients(block.Withdrawals, blockAccessList) : 0;

        if (!spec.RequestsEnabled) return reserve;

        if (spec.WithdrawalRequestsEnabled)
        {
            reserve += Queue(state, blockAccessList, spec.Eip7002ContractAddress, Eip7002Constants.WithdrawalRequestPredeployAddress,
                Eip7002Constants.CodeHash, MaxWithdrawalRequestsPerBlock, WithdrawalRequestSlots);
        }

        if (spec.ConsolidationRequestsEnabled)
        {
            reserve += Queue(state, blockAccessList, spec.Eip7251ContractAddress, Eip7251Constants.ConsolidationRequestPredeployAddress,
                Eip7251Constants.CodeHash, MaxConsolidationRequestsPerBlock, ConsolidationRequestSlots);
        }

        if (spec.BuilderRequestsEnabled)
        {
            reserve += Queue(state, blockAccessList, Eip8282Constants.BuilderDepositRequestPredeployAddress, Eip8282Constants.BuilderDepositRequestPredeployAddress,
                Eip8282Constants.BuilderDepositCodeHash, MaxBuilderDepositRequestsPerBlock, BuilderDepositRequestSlots);
            reserve += Queue(state, blockAccessList, Eip8282Constants.BuilderExitRequestPredeployAddress, Eip8282Constants.BuilderExitRequestPredeployAddress,
                Eip8282Constants.BuilderExitCodeHash, MaxBuilderExitRequestsPerBlock, BuilderExitRequestSlots);
        }

        return reserve;
    }

    private static long WithdrawalRecipients(Withdrawal[]? withdrawals, BlockAccessListItemSet blockAccessList)
    {
        if (withdrawals is null || withdrawals.Length == 0) return 0;

        HashSet<AddressAsKey> recipients = new(withdrawals.Length);
        foreach (Withdrawal withdrawal in withdrawals)
        {
            if (!blockAccessList.HasAccount(withdrawal.Address)) recipients.Add(withdrawal.Address);
        }
        return recipients.Count;
    }

    private static long Queue(IWorldState state, BlockAccessListItemSet blockAccessList, Address? contract, Address canonicalAddress,
        in ValueHash256 canonicalCodeHash, int maxPerBlock, int slotsPerRecord)
    {
        if (contract is null) return 0;

        long dequeued = maxPerBlock;
        if (contract == canonicalAddress && state.GetCodeHash(contract) == canonicalCodeHash)
        {
            UInt256 head = ReadWord(state, contract, QueueHeadSlot);
            UInt256 tail = ReadWord(state, contract, QueueTailSlot);
            UInt256 queued = tail > head ? tail - head : UInt256.Zero;
            dequeued = queued < (ulong)maxPerBlock ? (long)queued : maxPerBlock;
        }

        // The fixed words are counted even when already present; the bound only needs to be an upper one.
        long reserve = QueueFixedItems - 1 + dequeued * slotsPerRecord;
        if (!blockAccessList.HasAccount(contract)) reserve++;
        return reserve;
    }

    private static UInt256 ReadWord(IWorldState state, Address contract, in UInt256 slot)
    {
        state.Get(new StorageCell(contract, slot), out UInt256 value);
        return value;
    }
}
