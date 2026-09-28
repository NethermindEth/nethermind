// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Xdc.Contracts;

public interface IMasternodeVotingContract
{
    Address[] GetCandidatesByStake(BlockHeader blockHeader);
    Address[] GetCandidates(BlockHeader blockHeader);
    Address[] GetCandidates(ITransactionProcessor transactionProcessor, BlockHeader blockHeader);
    UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate);
    UInt256 GetCandidateStake(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate);
    Address GetCandidateOwner(BlockHeader blockHeader, Address candidate);
    Address GetCandidateOwner(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate);
    Address GetCandidateOwner(IWorldState worldState, Address candidate);

    /// <summary>Returns the addresses that have voted for <paramref name="candidate"/>.</summary>
    /// <remarks>
    /// Read straight from contract storage, as the reference client does. The voter list is unbounded, so an
    /// EVM call per entry would let one request amplify into arbitrarily many.
    /// <para>
    /// Not a set. <c>vote</c> appends only when the voter's balance is zero and <c>unvote</c> never removes the
    /// entry, so an address that voted, fully unvoted and voted again appears more than once while holding a
    /// single balance. Callers that aggregate over the result must count each address once.
    /// </para>
    /// </remarks>
    Address[] GetVoters(IWorldState worldState, Address candidate);

    /// <summary>Returns the amount <paramref name="voter"/> has staked on <paramref name="candidate"/>.</summary>
    /// <inheritdoc cref="GetVoters(IWorldState, Address)" path="/remarks"/>
    UInt256 GetVoterStake(IWorldState worldState, Address candidate, Address voter);
}
