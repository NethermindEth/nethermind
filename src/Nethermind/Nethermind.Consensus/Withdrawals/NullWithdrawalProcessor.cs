// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.Withdrawals;

public class NullWithdrawalProcessor : IWithdrawalProcessor
{
    public void ProcessWithdrawals(Block block, IReleaseSpec spec, ITxTracer? tracer = null) { }

    public static IWithdrawalProcessor Instance { get; } = new NullWithdrawalProcessor();
}
