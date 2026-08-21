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
    Address[] GetVoters(BlockHeader blockHeader, Address candidate);

    /// <inheritdoc cref="GetVoters(BlockHeader, Address)"/>
    Address[] GetVoters(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate);

    /// <summary>Returns the amount <paramref name="voter"/> has staked on <paramref name="candidate"/>.</summary>
    UInt256 GetVoterStake(BlockHeader blockHeader, Address candidate, Address voter);

    /// <inheritdoc cref="GetVoterStake(BlockHeader, Address, Address)"/>
    UInt256 GetVoterStake(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate, Address voter);
}
