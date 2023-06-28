// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Optimism;

public interface IL1CostHelper
{
    Address L1FeeReceiver { get; }

    UInt256 ComputeL1Cost(Transaction tx, IWorldState worldState, long number, ulong timestamp, bool isDeposit);
}
