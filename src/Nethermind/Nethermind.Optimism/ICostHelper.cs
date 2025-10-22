// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Optimism;

public interface ICostHelper
{
    UInt256 ComputeL1Cost(Transaction tx, BlockHeader header, IWorldState worldState);

    UInt256 ComputeOperatorCost(long gas, BlockHeader header, IWorldState worldState);

    UInt256 ComputeDAFootprint(Transaction tx, BlockHeader header, IWorldState worldState);
}
