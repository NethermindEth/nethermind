// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Optimism;

public interface ICostHelper
{
    UInt256 ComputeL1Cost(Transaction tx, BlockHeader header, IWorldState worldState);

    UInt256 ComputeOperatorCost(long gas, BlockHeader header, IWorldState worldState);
}
