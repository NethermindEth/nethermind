// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Specs;

namespace Nethermind.Consensus.Withdrawals;

public interface IWithdrawalProcessor
{
    void ProcessWithdrawals(Block block, IReleaseSpec spec);
}
