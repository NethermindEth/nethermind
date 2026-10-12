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
/// The contracts the spec names are checked against it by <c>PostTransactionWritersTests</c>, which walks every
/// contract address of every fork, so a fork that adds one fails the test until it is handled.
/// The addresses that are constants rather than spec properties, the EIP-8282 predeploys, are listed by hand and a
/// fork that adds another one of those has to be added here by hand too.
/// Only the seal engines in <see cref="Describes"/> are described at all: a chain whose plugin replaces the
/// withdrawal processor or writes more after the transactions is left to the replay.</summary>
internal static class PostTransactionWriters
{
    /// <summary>The seal engines whose block processing is the one described here: the standard processor, crediting
    /// withdrawals to their recipients and calling only the contracts the spec names. An AuRa chain credits
    /// withdrawals through a chainspec contract instead, which writes storage no spec property can name; Optimism
    /// and Taiko carry their own processors. Their blocks are never chained.
    /// The answer is only as good as the provider: <see cref="ISpecProvider.SealEngine"/> is a default interface
    /// implementation returning <see cref="SealEngineType.Ethash"/>, so a provider that adds a block processing step
    /// of its own and leaves the property alone would be taken for a standard chain. Every provider in the tree
    /// overrides it, through its chain spec or explicitly.</summary>
    public static bool Describes(string sealEngine) =>
        sealEngine is SealEngineType.Ethash or SealEngineType.BeaconChain or SealEngineType.Clique or SealEngineType.NethDev;

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
        // Refused whether or not the block's processing writes it: the deposit contract is read from the receipts
        // rather than called, but one more refused address costs a read of the parent state and keeps the rule that
        // every contract the spec names is refused, which is what the test can check for a fork nobody has written yet.
        Add(writers, spec.DepositContractAddress);
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
