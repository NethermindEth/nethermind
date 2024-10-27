// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Consensus;
using Nethermind.Init.Steps;

namespace Nethermind.Taiko;

// Based rollups provide transaction lists instead of direct block production
public class InitializeBlockProducerTaiko(TaikoNethermindApi api) : InitializeBlockProducer(api)
{
    protected override IBlockProducer BuildProducer()
    {
        throw new InvalidOperationException("Taiko block production should not be initialized.");
    }
}
