// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Consensus.Requests;

public class ConsensusRequestsProcessor : IConsensusRequestsProcessor
{
    private readonly WithdrawalRequestsProcessor _withdrawalRequestsProcessor = new();
    private readonly IDepositsProcessor _depositsProcessor = new DepositsProcessor();

    public void ProcessRequests(IReleaseSpec spec, IWorldState state, Block block, TxReceipt[] receipts)
    {
        if (!spec.DepositsEnabled && !spec.WithdrawalRequestsEnabled)
            return;

        using ArrayPoolList<ConsensusRequest> requestsList = new(receipts.Length * 2);

        // Process deposits
        requestsList.AddRange(_depositsProcessor.ProcessDeposits(block, receipts, spec));
        requestsList.AddRange(_withdrawalRequestsProcessor.ReadWithdrawalRequests(spec, state, block));

        ConsensusRequest[] requests = requestsList.ToArray();
        Hash256 root = new RequestsTrie(requests).RootHash;
        block.Body.Requests = requests;
        block.Header.RequestsRoot = root;
    }
}
