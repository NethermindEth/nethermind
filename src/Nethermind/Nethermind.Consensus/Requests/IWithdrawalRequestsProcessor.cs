// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.Requests;

public interface IWithdrawalRequestsProcessor
{
    IEnumerable<WithdrawalRequest> ReadWithdrawalRequests(IReleaseSpec spec, IWorldState state, Block block);
}
