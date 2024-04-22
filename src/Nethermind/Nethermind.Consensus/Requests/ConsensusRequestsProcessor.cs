// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Requests;

public class ConsensusRequestsProcessor
{
    private readonly WithdrawalRequestsProcessor _withdrawalRequestsProcessor;
    private readonly IDepositsProcessor _depositsProcessor;

    public ConsensusRequestsProcessor()
    {
        _withdrawalRequestsProcessor = new WithdrawalRequestsProcessor();
        _depositsProcessor = new DepositsProcessor();
    }
    public void ProcessRequests(IReleaseSpec spec, IWorldState state, Block block, TxReceipt[] receipts)
    {
        // Process deposits
        var deposits =  _depositsProcessor.ProcessDeposits(block, receipts, spec);


        // Process withdrawal requests
        if (spec.IsEip7002Enabled)
        {
            ValidatorExit[]? withdrawalRequests = _withdrawalRequestsProcessor.ReadWithdrawalRequests(spec, state);
            Hash256 root = ValidatorExitsTrie.CalculateRoot(withdrawalRequests);
            block.Body.ValidatorExits = withdrawalRequests;
            block.Header.ValidatorExitsRoot = root;
        }

    }
}
