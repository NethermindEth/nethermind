// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using MathNet.Numerics.Distributions;
using Nethermind.Consensus.Requests;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using Nethermind.State.Proofs;

namespace Nethermind.Merge.Plugin.Test;

public class ConsensusRequestsProcessorMock : IConsensusRequestsProcessor
{
    public ConsensusRequest[] Requests =
    [
        TestItem.DepositA_1Eth,
        TestItem.DepositB_2Eth,
        TestItem.WithdrawalRequestA,
        TestItem.WithdrawalRequestB
    ];

    public void ProcessRequests(IReleaseSpec spec, IWorldState state, Block block, TxReceipt[] receipts)
    {
        if (block.IsGenesis)
            return;

        block.Body.Requests = Requests;
        Hash256 root = new RequestsTrie(Requests).RootHash;
        block.Header.RequestsRoot = root;
    }
}
