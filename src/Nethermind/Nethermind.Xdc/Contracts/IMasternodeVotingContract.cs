// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;

namespace Nethermind.Xdc.Contracts;

public interface IMasternodeVotingContract
{
    Address[] GetCandidatesByStake(BlockHeader blockHeader);
    Address[] GetCandidates(BlockHeader blockHeader);
    UInt256 GetCandidateStake(BlockHeader blockHeader, Address candidate);
    Address GetCandidateOwner(BlockHeader blockHeader, Address candidate);
    Address GetCandidateOwnerDuringProcessing(ITransactionProcessor transactionProcessor, BlockHeader blockHeader, Address candidate);
}
