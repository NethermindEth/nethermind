// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.State.Flat.History.Changesets;

/// <summary>Every address a block can write after its last transaction. The changeset rows describe the
/// transactions only, so a chain of traced blocks must never answer for one of these: the value the block left is in
/// the state, not in the rows.
/// The set follows <see cref="Nethermind.Consensus"/>'s block processing: the beneficiary (the reward), the
/// withdrawal recipients, the system contracts called around the transactions (EIP-4788 beacon root, EIP-2935 block
/// hashes) and those the execution-requests processor dequeues from afterwards (EIP-7002 withdrawal requests,
/// EIP-7251 consolidations, EIP-8282 builder deposits and exits), plus the account a chain spec exempts from
/// EIP-158 pruning, which a system call can leave written.
/// A fork that adds a system contract has to be added here; <c>PostTransactionWritersTests</c> is what says so,
/// by naming every address the execution-requests processor can target.</summary>
internal static class PostTransactionWriters
{
    /// <summary>False when the block cannot be described, so nothing of it is chained.</summary>
    public static bool TryCollect(Block block, IReleaseSpec spec, HashSet<AddressAsKey> writers)
    {
        if (block.Beneficiary is null) return false;

        writers.Add(block.Beneficiary);
        AddSystemContracts(spec, writers);

        if (block.Withdrawals is { } withdrawals)
        {
            foreach (Withdrawal withdrawal in withdrawals) writers.Add(withdrawal.Address);
        }

        return true;
    }

    /// <summary>The fork's system contracts, whichever of them it enables.</summary>
    public static void AddSystemContracts(IReleaseSpec spec, HashSet<AddressAsKey> writers)
    {
        Add(writers, spec.Eip4788ContractAddress);
        Add(writers, spec.Eip2935ContractAddress);
        Add(writers, spec.Eip7002ContractAddress);
        Add(writers, spec.Eip7251ContractAddress);
        Add(writers, Eip8282Constants.BuilderDepositRequestPredeployAddress);
        Add(writers, Eip8282Constants.BuilderExitRequestPredeployAddress);
        Add(writers, spec.Eip158IgnoredAccount);
    }

    private static void Add(HashSet<AddressAsKey> writers, Address? address)
    {
        if (address is not null) writers.Add(address);
    }
}
