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
/// Upper bound on the EIP-7928 block access list items that the end-of-block steps can add after transaction
/// selection, so a block producer can stop selecting transactions before the block exceeds
/// <c>block_gas_limit / ITEM_COST</c>.
/// </summary>
/// <remarks>
/// <para>
/// After the transactions, a block only runs the withdrawals and the request-queue system calls
/// (EIP-7002, EIP-7251, EIP-8282). Neither spends block gas, so their items are not covered by the
/// transactions' gas and have to be reserved:
/// </para>
/// <list type="bullet">
/// <item>Withdrawals credit their recipients: one item per distinct recipient not already in the list.</item>
/// <item>
/// Each queue contract is one address item plus the four words below <c>QUEUE_STORAGE_OFFSET</c> (excess, count,
/// head, tail), read or written on every call. It then dequeues up to its per-block maximum of records, oldest first,
/// reading every slot of each. Records enqueued by this block's transactions were written by them, so their slots are
/// already in the list; only records queued before the block (<c>tail - head</c> in the parent state) can add items.
/// </item>
/// </list>
/// <para>
/// Record sizes (EIP-7002 <c>add_withdrawal_request</c>, EIP-7251 <c>add_consolidation_request</c>, EIP-8282
/// "Deposit requests"/"Exit requests"): withdrawal <c>20 + 48 + 8</c> bytes in 3 slots, consolidation
/// <c>20 + 48 + 48</c> bytes in 4 slots, builder deposit 184 bytes in 6 slots, builder exit <c>20 + 48</c> bytes in 3 slots.
/// The queue length is read only from canonical bytecode at the canonical address; any other code is assumed to drain
/// its per-block maximum with the same layout.
/// </para>
/// <para>
/// The pre-execution system calls (EIP-4788, EIP-2935, the EIP-8253 fork-block transition) run before the
/// transactions and are already in the list when selection starts.
/// </para>
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
    public static long Calculate(Block block, IReleaseSpec spec, IWorldState state, GeneratedBlockAccessList blockAccessList)
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

    private static long WithdrawalRecipients(Withdrawal[]? withdrawals, GeneratedBlockAccessList blockAccessList)
    {
        if (withdrawals is null || withdrawals.Length == 0) return 0;

        HashSet<AddressAsKey> recipients = new(withdrawals.Length);
        foreach (Withdrawal withdrawal in withdrawals)
        {
            if (!blockAccessList.HasAccount(withdrawal.Address)) recipients.Add(withdrawal.Address);
        }
        return recipients.Count;
    }

    private static long Queue(IWorldState state, GeneratedBlockAccessList blockAccessList, Address? contract, Address canonicalAddress,
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
