// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Consensus.Producers;

public class NeverProduceTrigger : IBlockProductionTrigger
{
    public static readonly NeverProduceTrigger Instance = new();

    public event EventHandler<BlockProductionEventArgs>? TriggerBlockProduction
    {
        add { }
        remove { }
    }
}
