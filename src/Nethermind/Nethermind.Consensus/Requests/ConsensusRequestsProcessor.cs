// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Requests;

public class ConsensusRequestsProcessor : IConsensusRequestsProcessor
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
        if (spec.IsEip6110Enabled == false && spec.IsEip7002Enabled == false)
            return;

        List<ConsensusRequest> requestsList = [];
        // Process deposits
        List<Deposit>? deposits = _depositsProcessor.ProcessDeposits(block, receipts, spec);
        if (deposits is { Count: > 0 })
            requestsList.AddRange(deposits);

        WithdrawalRequest[]? withdrawalRequests = _withdrawalRequestsProcessor.ReadWithdrawalRequests(spec, state, block);
        if (withdrawalRequests is { Length: > 0 })
            requestsList.AddRange(withdrawalRequests);

        ConsensusRequest[]? requests = requestsList.ToArray();
        Hash256 root = new RequestsTrie(requests).RootHash;
        block.Body.Requests = requests; // ToDo think about it
        block.Header.RequestsRoot = root;
    }
}
