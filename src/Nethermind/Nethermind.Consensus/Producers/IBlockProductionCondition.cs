// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus.Producers;

public interface IBlockProductionCondition
{
    bool CanProduce(BlockHeader parentHeader);
}

public class AlwaysOkBlockProductionCondition : IBlockProductionCondition
{
    public static IBlockProductionCondition Instance = new AlwaysOkBlockProductionCondition();

    public bool CanProduce(BlockHeader parentHeader)
    {
        return true;
    }
}
