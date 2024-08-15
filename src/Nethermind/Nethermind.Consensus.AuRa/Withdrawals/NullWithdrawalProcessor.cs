// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Withdrawals;

public class NullWithdrawalProcessor : IWithdrawalProcessor
{
    public void ProcessWithdrawals(Block block, IReleaseSpec spec, IWorldState worldState) { }

    public static IWithdrawalProcessor Instance { get; } = new NullWithdrawalProcessor();
}
