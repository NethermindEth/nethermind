// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.ConsensusRequests;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Blockchain.ConsensusRequests;

public interface IWithdrawalRequestsProcessor
{
    ValidatorExit[] ReadWithdrawalRequests(IReleaseSpec spec, IWorldState state);
}
