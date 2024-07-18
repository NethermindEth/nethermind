// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.AuRa.Withdrawals;

public class NullWithdrawalProcessor : IWithdrawalProcessor
{
    public void ProcessWithdrawals(Block block, IReleaseSpec spec) { }

    public static IWithdrawalProcessor Instance { get; } = new NullWithdrawalProcessor();
}
