// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Consensus.Withdrawals;

public interface IWithdrawalProcessor
{
    void ProcessWithdrawals(Block block, IBlockTracer blockTracer, IReleaseSpec spec, IWorldState worldState);
}
