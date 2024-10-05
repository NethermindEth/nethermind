// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

public interface IConsensusRequestsProcessor
{
    void ProcessRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec);
}
